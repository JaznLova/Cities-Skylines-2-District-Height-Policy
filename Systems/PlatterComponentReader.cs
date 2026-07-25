using System;
using System.Reflection;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using DistrictHeightPolicy;

namespace DistrictMod.Systems
{
    // Reads an IComponentData whose type is only known at runtime — Platter's
    // LinkedParcel { Entity m_Parcel } — from a managed system, without generic reflection.
    //
    // The obvious route, caching typeof(EntityManager).GetMethod("GetComponentData")
    // .MakeGenericMethod(type) and invoking it per entity, allocates twice on every call (the
    // object[] arguments array and the boxed struct) and dispatches through reflection twice.
    // DistrictHeightPolicySystem calls this from inside GameSimulation, so that is GC pressure in
    // the wrong place. Instead the entity's chunk is located and its raw memory reinterpreted as
    // Entity: no invoke, no boxing, no allocation, and only an archetype test for a building that
    // has no such component at all.
    //
    // The reinterpret is only valid while the component really is one Entity wide, which is
    // exactly the assumption a Platter update could break. VerifyLayout checks it up front and the
    // reader refuses to operate — loudly, once — rather than reading whatever now sits at offset 0.
    internal sealed class SingleEntityComponentReader
    {
        private readonly Type m_Type;
        private ComponentType m_Component;
        private DynamicComponentTypeHandle m_Handle;

        // Exists only to be able to complete the jobs writing this component; nothing is read
        // through it. See TryUpdate.
        private EntityQuery m_SyncQuery;

        private bool m_Ready;
        private bool m_Broken;
        private bool m_LoggedFirstHit;

        public bool IsUsable => m_Ready && !m_Broken;

        public SingleEntityComponentReader(Type type)
        {
            m_Type = type;
            m_Broken = type == null || !VerifyLayout(type);

            if (type != null && m_Broken)
                PlatterInterop.Fault($"{type.FullName} is no longer a single Entity field — " +
                                     "parcel detection disabled");
        }

        private static bool VerifyLayout(Type type)
        {
            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public
                                        | BindingFlags.NonPublic);
            return fields.Length == 1 && fields[0].FieldType == typeof(Entity);
        }

        // Must be called once per OnUpdate before any Read, and returns false when the component
        // is not usable this tick. Setup happens here rather than in the owner's OnCreate because
        // Platter's types are not resolvable that early.
        public bool TryUpdate(SystemBase system)
        {
            if (m_Broken) return false;

            try
            {
                if (!m_Ready)
                {
                    // Throws if the type was never registered with TypeManager. Platter's own
                    // systems query it, so it is — but a wrong type would otherwise surface as a
                    // hard crash rather than a disabled feature.
                    m_Component = ComponentType.ReadOnly(m_Type);

                    // EntityManager.CreateEntityQuery, not the system's GetEntityQuery: the latter
                    // also declares the access on the system, and doing that mid-OnUpdate mutates
                    // the dependency set the scheduler has already computed for this frame.
                    m_SyncQuery = system.EntityManager.CreateEntityQuery(m_Component);
                    m_Handle = system.EntityManager.GetDynamicComponentTypeHandle(m_Component);
                    m_Ready = true;
                    Mod.log.Info($"[PlatterComponentReader] Reading {m_Type.FullName}.");
                }

                m_Handle.Update(system);

                // The standalone query declares no access on this system, so the safety system
                // will not complete Platter's jobs on our behalf. One completion per tick does.
                m_SyncQuery.CompleteDependency();
                return true;
            }
            catch (Exception e)
            {
                m_Broken = true;
                PlatterInterop.Fault($"dynamic read of {m_Type?.FullName} unavailable: {e.Message}");
                return false;
            }
        }

        // The component's Entity field, or Entity.Null when the entity does not carry it. Note
        // that for LinkedParcel a present component with a null field is normal and means "this
        // growable is not on a parcel", so both cases collapse to the same answer.
        public Entity Read(EntityManager em, Entity entity)
        {
            if (!IsUsable) return Entity.Null;
            if (!em.HasComponent(entity, m_Component)) return Entity.Null;

            var info = em.GetStorageInfo(entity);
            // The handle goes in by ref — the by-value overload is obsolete in Entities 1.0.
            var array = info.Chunk.GetDynamicComponentDataArrayReinterpret<Entity>(
                ref m_Handle, UnsafeUtility.SizeOf<Entity>());
            var value = array[info.IndexInChunk];

            if (value != Entity.Null && !m_LoggedFirstHit)
            {
                m_LoggedFirstHit = true;
                Mod.log.Info($"[PlatterComponentReader] First non-null {m_Type.Name}: " +
                             $"entity {entity.Index} -> {value.Index}.");
            }

            return value;
        }

        public void Dispose()
        {
            if (!m_Ready) return;
            m_SyncQuery.Dispose();
            m_Ready = false;
        }
    }
}

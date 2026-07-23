import { ModRegistrar } from "cs2/modding";
import { HeightPolicyPanel } from "./HeightPolicyPanel";

const register: ModRegistrar = (moduleRegistry) => {
    moduleRegistry.append("Game", HeightPolicyPanel);
    console.log("DistrictMod UI registered.");
};

export default register;

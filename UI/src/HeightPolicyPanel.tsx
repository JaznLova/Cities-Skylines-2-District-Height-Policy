import React from "react";
import { bindValue, trigger, useValue } from "cs2/api";

const GROUP = "districtHeightPolicy";

const isDistrict$ = bindValue<boolean>(GROUP, "isDistrict", false);
const activeTiers$ = bindValue<string>(GROUP, "activeTiers", "");
const allTiers$ = bindValue<string>(GROUP, "allTiers", "");

const TIER_LABELS: Record<string, string> = {
    Small:      "Small (0–24m)",
    Medium:     "Medium (24–32m)",
    Large:      "Large (32–52m)",
    Tall:       "Tall (52–68m)",
    SuperTall:  "Super Tall (68–115m)",
    Skyscraper: "Skyscraper (115m+)",
};

export const HeightPolicyPanel = () => {
    const isDistrictSelected = useValue(isDistrict$);
    const activeTiersStr = useValue(activeTiers$);
    const allTiersStr = useValue(allTiers$);

    if (!isDistrictSelected) return null;

    const allTiers = allTiersStr ? allTiersStr.split(",").filter(Boolean) : [];
    const activeTiers = new Set(activeTiersStr ? activeTiersStr.split(",").filter(Boolean) : []);

    return (
        <div style={{
            position: "absolute",
            bottom: "220rem",
            right: "20rem",
            background: "rgba(30,30,30,0.95)",
            color: "#e8e8e8",
            borderRadius: "6rem",
            padding: "12rem 16rem",
            minWidth: "220rem",
            fontFamily: "sans-serif",
            fontSize: "13rem",
            boxShadow: "0 2rem 8rem rgba(0,0,0,0.5)",
            zIndex: 500,
            pointerEvents: "auto",
        }}>
            <div style={{
                fontWeight: "bold",
                fontSize: "14rem",
                marginBottom: "10rem",
                borderBottom: "1rem solid #555",
                paddingBottom: "6rem",
            }}>
                Height Policy
            </div>
            {allTiers.map(tier => {
                const active = activeTiers.has(tier);
                return (
                    <div
                        key={tier}
                        style={{
                            display: "flex",
                            alignItems: "center",
                            gap: "8rem",
                            padding: "5rem 4rem",
                            cursor: "pointer",
                            borderRadius: "4rem",
                            pointerEvents: "auto",
                        }}
                        onMouseDown={() => trigger(GROUP, "toggleTier", tier)}
                    >
                        <div style={{
                            width: "16rem",
                            height: "16rem",
                            border: `2rem solid ${active ? "#4a9eff" : "#888"}`,
                            borderRadius: "3rem",
                            display: "flex",
                            alignItems: "center",
                            justifyContent: "center",
                            flexShrink: 0,
                            background: active ? "#4a9eff" : "transparent",
                            color: "#fff",
                            fontSize: "11rem",
                            fontWeight: "bold",
                        }}>
                            {active ? "✓" : ""}
                        </div>
                        <span style={{ pointerEvents: "none" }}>
                            {TIER_LABELS[tier] || tier}
                        </span>
                    </div>
                );
            })}
        </div>
    );
};

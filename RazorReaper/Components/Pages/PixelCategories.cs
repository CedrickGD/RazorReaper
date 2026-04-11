namespace RazorReaper.Components.Pages
{
    public static class PixelTextureCategories
    {
        public static Dictionary<string, Dictionary<string, string[]>> GetCategories(string shooterGamePath)
        {
            return new Dictionary<string, Dictionary<string, string[]>>
            {
                ["🏹 Weapons - Harpoon"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponHarpoon", "Textures"), new[] {
                          "T_HarpoonProjectile_Net_N.uasset",
                          "T_HarpoonProjectile_Net_Layered.uasset",
                          "T_HarpoonProjectile_Net_BC.uasset",
                          "T_Harpoon_N.uasset",
                          "T_Harpoon_Layered.uasset",
                          "T_Arrow_N.uasset",
                          "T_Arrow_layered.uasset",
                          "T_Arrow_D.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponHarpoon"), new[] {
                          "SK_HarpoonProjectile_Net.uasset",
                          "SM_Harpoon.uasset",
                          "SM_HarpoonAmmo_Net.uasset",
                          "SM_HarpoonProjectile.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponHarpoon", "Projectile"), new[] {
                          "HarpoonProjectile_Net.uasset",
                          "HarpoonProjectile_Net_Inst.uasset"
                    }}
                },
                ["🔫 Weapons - Tek Rifle"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponTekRifle"), new[] {
                          "M_ScopeOverlay.uasset",
                          "M_ScopeOverlay_Inst.uasset",
                          "MF_TekRifle_Lens.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponTekRifle", "Textures"), new[] {
                          "T_TekRifle_Colorize_d.uasset",
                          "T_TekRifle_Colorize_m.uasset",
                          "T_TekRifle_Layered.uasset",
                          "T_TekRifle_N.uasset",
                          "T_TekRifle_Scope_Colorize_d.uasset",
                          "T_TekRifle_Scope_Colorize_m.uasset",
                          "T_TekRifle_Scope_Layered.uasset",
                          "T_TekRifle_Scope_N.uasset"
                    }}
                },
                ["🛡️ Weapons - Shields"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponShieldMetal"), new[] {
                          "T_MetalShield_Colorize_d.uasset",
                          "T_MetalShield_Colorize_m.uasset",
                          "T_MetalShield_Layered.uasset",
                          "T_MetalShield_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponRiotShieldTransparent"), new[] {
                          "SM_TransparentRiotShield.uasset",
                          "T_TransparentRiotShield_Colorization_d.uasset",
                          "T_TransparentRiotShield_Colorization_m.uasset",
                          "T_TransparentRiotShield_LP_D.uasset",
                          "T_TransparentRiotShield_LP_Layered.uasset",
                          "T_TransparentRiotShield_LP_N.uasset",
                          "RiotShield_Transparent_Colorize_MIC.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponWoodShield"), new[] {
                          "sm_WoodShield.uasset",
                          "T_WoodShield_Colorize_d.uasset",
                          "T_WoodShield_Colorize_m.uasset",
                          "T_WoodShield_Layered.uasset",
                          "T_WoodShield_N.uasset",
                          "WoodShield_colorize_MIC.uasset"
                    }}
                },
                ["⛏️ Tools - Picks & Axes"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponMetalPick"), new[] {
                          "T_MetalPick_layered.uasset",
                          "T_MetalPick_Normal_.uasset",
                          "MetalPick_colorize_d.uasset",
                          "MetalPick_colorize_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponHatchet"), new[] {
                          "Hatchet_normal.uasset",
                          "Hatchet_Material.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponMetalAxe"), new[] {
                          "T_MetalAxe_Normal.uasset",
                          "T_MetalAxe_Layered.uasset",
                          "MetalAxe_Colorize_m.uasset",
                          "MetalAxe_Colorize_d.uasset",
                          "axe.uasset"
                    }}
                },
                ["🔪 Weapons - Melee"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponMetalSickle"), new[] {
                          "MetalSickle_Colorize_m.uasset",
                          "T_MetalSickle_d.uasset",
                          "T_MetalSickle_Layered.uasset",
                          "T_MetalSickle_n.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponSword", "textures"), new[] {
                          "T_SwordForged_N.uasset",
                          "T_SwordForged_Layered.uasset",
                          "T_SwordForged_D.uasset",
                          "T_SwordForged_Colorize_m.uasset",
                          "T_SwordForged_Colorize_d.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponSword"), new[] {
                          "SwordForged_Colorize_MIC.uasset",
                          "SM_SwordForged.uasset",
                          "MM_Equipment_Metallic_MICMeshP.uasset",
                          "MM_Equipment_Metallic_MIC.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponWoodClub"), new[] {
                          "T_WoodClub_Colorize_d.uasset",
                          "T_WoodClub_Colorize_m.uasset",
                          "T_WoodClub_N.uasset",
                          "T_WoodClub_Layered.uasset",
                          "WoodClub_colorize_MIC.uasset"
                    }}
                },
                ["🔫 Weapons - Guns (Basic)"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponMachinedShotgun"), new[] {
                          "T_shotgun_FP_Layered.uasset",
                          "T_shotgun_FP_N.uasset",
                          "T_shotgunMachined_Colorize_d.uasset",
                          "T_shotgunMachined_Colorize_m.uasset",
                          "Shotgun_Machined_Colorize_MIC.uasset",
                          "T_shotgun__FPV_N.uasset",
                          "T_shotgun_FPV_Layered.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponPistol", "Colorization"), new[] {
                          "pistol_set_d.uasset",
                          "pistol_set_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponPistol"), new[] {
                          "Pistol.uasset",
                          "Pistol_layered.uasset",
                          "Pistol_Mat_Colorize.uasset",
                          "Pistol_Normal.uasset",
                          "Pistol_SM.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponPistolMachined", "textures"), new[] {
                          "T_PistolMachined_Colorize_m.uasset",
                          "T_PistolMachined_Colorize_d.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponPistolMachined"), new[] {
                          "PistolMachined_Colorized_MIC.uasset",
                          "PistolMachined_MIC.uasset"
                    }}
                },
                ["🎯 Weapons - Sniper & Rifles"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponMachinedSniper"), new[] {
                          "T_SniperRifle_Colorize_d.uasset",
                          "T_SniperRifle_Colorize_m.uasset",
                          "T_SniperRifle_D.uasset",
                          "T_SniperRifle_Layered.uasset",
                          "T_SniperRifle_N.uasset",
                          "T_SniperScope_Colorize_d.uasset",
                          "T_SniperScope_Colorize_m.uasset",
                          "T_SniperScope_Layered.uasset",
                          "T_SniperScope_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponOneShotRifle", "textures"), new[] {
                          "SSR_colorize_d.uasset",
                          "SSR_colorize_m.uasset",
                          "SSR_Layered.uasset",
                          "SSR_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponOneShotRifle", "material"), new[] {
                          "SSR_Mat_Colorize.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponOneShotRifle", "scope"), new[] {
                          "Scope_D.uasset",
                          "Scope_M.uasset",
                          "Scope_N.uasset",
                          "Scope_R.uasset",
                          "Scope_AO.uasset",
                          "Scope_layered.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponOneShotRifle"), new[] {
                          "M_HoloCrosshair.uasset",
                          "M_HoloScopeOverlay.uasset",
                          "M_ScopeOverlay.uasset",
                          "M_ScopeOverlay_Spyglass.uasset"
                    }}
                },
                ["🏹 Weapons - Bows & Crossbows"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponCompoundBow", "Textures"), new[] {
                          "T_CompoundBow_Colorize_d.uasset",
                          "T_CompoundBow_Colorize_m.uasset",
                          "T_CompoundBow_Layered.uasset",
                          "T_CompoundBow_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponCrossbow", "Textures"), new[] {
                          "Crossbow_Arrow_Metal_D.uasset",
                          "Crossbow_Arrow_Metal_Layered.uasset",
                          "Crossbow_Arrow_Metal_N.uasset",
                          "Crossbow_Arrow_Sedative_D.uasset",
                          "Crossbow_Arrow_Sedative_Layered.uasset",
                          "Crossbow_Arrow_Sedative_N.uasset",
                          "Crossbow_Arrow_Stone_D.uasset",
                          "Crossbow_Arrow_Stone_Layered.uasset",
                          "Crossbow_Arrow_Stone_N.uasset",
                          "Crossbow_Colorize_d.uasset",
                          "Crossbow_Colorize_m.uasset",
                          "Crossbow_Layered.uasset",
                          "Crossbow_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponBow", "textures"), new[] {
                          "Arrow_AO.uasset",
                          "Arrow_base_color.uasset",
                          "Arrow_base_color_Green.uasset",
                          "Arrow_normal.uasset",
                          "Arrow_roughness.uasset",
                          "Bow_Low_layered.uasset",
                          "Bow_Low_normal.uasset",
                          "LRope_layered.uasset",
                          "LRope_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponBow", "Colorization"), new[] {
                          "Bow_colorize_d.uasset",
                          "Bow_colorize_m.uasset",
                          "Bow_String_colorize_d.uasset",
                          "GreenMask.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponBow"), new[] {
                          "Bow_Test.uasset",
                          "Bow_Rope_colorize_MIC.uasset",
                          "Bow_Colorize_MIC.uasset",
                          "Arrow01_SM.uasset",
                          "Arrow_MIC_Green.uasset",
                          "Arrow_MIC.uasset"
                    }}
                },
                ["💥 Weapons - Explosives & Rockets"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponRocketLauncher", "Textures"), new[] {
                          "RocketLauncher_d.uasset",
                          "RocketLauncher_Layered.uasset",
                          "RocketLauncher_n.uasset",
                          "T_rocketLauncher_D.uasset",
                          "T_rocketLauncher_N.uasset",
                          "T_rocketLauncher_M.uasset",
                          "T_rocketLauncher_Layered.uasset",
                          "T_rocketLauncher_Colorize_d.uasset",
                          "T_rocketLauncher_Colorize_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponRocketLauncher"), new[] {
                          "rocket.uasset",
                          "rocketLauncher_MIC.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Test_Henrique", "RocketLauncher", "Textures"), new[] {
                          "T_RocketLauncher_N.uasset",
                          "T_RocketLauncher_Layered.uasset",
                          "T_RocketLauncher_E.uasset",
                          "T_RocketLauncher_Colorize_m.uasset",
                          "T_RocketLauncher_Colorize_d.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Test_Henrique", "RocketLauncher"), new[] {
                          "RocketLauncher_Colorize_Emissive_MIC.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponC4"), new[] {
                          "C4_FPV_RIG.uasset",
                          "C4_TPV_RIG.uasset",
                          "detonator_TPV_RIG.uasset",
                          "C4_explosive.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponC4", "Textures"), new[] {
                          "C4Detonator_E.uasset",
                          "C4Detonator_layered.uasset",
                          "C4Detonator_N.uasset",
                          "Explosive_BC.uasset",
                          "Explosive_E.uasset",
                          "Explosive_Layered.uasset",
                          "Explosive_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponC4", "Colorization"), new[] {
                          "C4Detonator_colorize_d.uasset",
                          "C4Detonator_colorize_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponC4", "Materials"), new[] {
                          "C4Detonator_Colorize_MIC.uasset",
                          "Explosive_MlC_Active.uasset",
                          "Explosive_MlC.uasset"
                    }}
                },
                ["🪝 Weapons - Special Tools"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponGrapHook"), new[] {
                          "SM_GrappHook_Arrow.uasset",
                          "M_Cable.uasset",
                          "M_HideMesh.uasset",
                          "T_GrapHook_D.uasset",
                          "T_GrapHook_Layered.uasset",
                          "T_GrapHook_N.uasset",
                          "TempReelInCue.uasset",
                          "TempReelOutCue.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponGrapHook", "Projectile"), new[] {
                          "T_GrapProjectile_D.uasset",
                          "T_GrapProjectile_Layered.uasset",
                          "T_GrapProjectile_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponBola"), new[] {
                          "SM_Bola_thrown.uasset",
                          "SM_Bola_wrapped.uasset",
                          "T_Bola_D.uasset",
                          "T_Bola_Layered.uasset",
                          "T_Bola_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponLasso"), new[] {
                          "T_Lasso_Layered.uasset",
                          "T_Lasso_N.uasset",
                          "T_Lasso_D.uasset",
                          "SM_Lasso_thrown.uasset",
                          "SM_Lasso_spinning.uasset",
                          "MM_Lasso.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponTorch"), new[] {
                          "Torch_DroppedItem_SM.uasset",
                          "Torch_SM.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponTorch", "Textures"), new[] {
                          "Torch_layered.uasset",
                          "torch_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponTorch", "colorization"), new[] {
                          "Torch_colorize_d.uasset",
                          "Torch_colorize_m.uasset"
                    }}
                },
                ["🗡️ Weapons - Spears & Attachments"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "WeaponMetalSpear", "Pitchfork"), new[] {
                          "T_Pitchfork_Colorize_d.uasset",
                          "T_Pitchfork_Layered.uasset",
                          "T_Pitchfork_M.uasset",
                          "T_Pitchfork_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponMetalSpear"), new[] {
                          "T_MetalSpear_Colorize_d.uasset",
                          "T_MetalSpear_Layered.uasset",
                          "T_MetalSpear_M.uasset",
                          "T_MetalSpear_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponSilencer"), new[] {
                          "MI_Silencer.uasset",
                          "Silencer_colorize_m.uasset",
                          "SM_Silencer.uasset",
                          "T_Silencer_D.uasset",
                          "T_Silencer_Layered.uasset",
                          "T_Silencer_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponSpyglass", "Textures"), new[] {
                          "T_Spyglass_D.uasset",
                          "T_Spyglass_Layered.uasset",
                          "T_Spyglass_N.uasset"
                    }}
                },
                ["🤖 Armor - Tek Suit"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Tek"), new[] {
                          "Tek_Colorize_BaseMIC.uasset",
                          "Tek_Armor_Emissive_Colorize_MIC.uasset",
                          "Tek_Male_gloves_FPV_skin_m.uasset",
                          "Tek_Male_gloves_skin_m.uasset",
                          "Tek_Male_pants_skin_m.uasset",
                          "Tek_Male_shirt_skin_m.uasset",
                          "Tek_jetpack_Colorize_MIC.uasset",
                          "Tek_shoes_Colorize_MIC.uasset",
                          "Tek_shirt_Colorize_MIC.uasset",
                          "Tek_pants_Colorize_MIC.uasset",
                          "Tek_gloves_Colorize_MIC.uasset",
                          "tek_Gloves_FPV_Colorize_MIC.uasset",
                          "Tek_helmet_Colorize_MIC.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Tek", "Textures"), new[] {
                          "tek_armor_helmet_Layered.uasset",
                          "tek_armor_pants_Layered.uasset",
                          "tek_armor_shirt_Layered.uasset",
                          "tek_armor_shoes_Layered.uasset",
                          "tek_armor_gloves_Layered.uasset",
                          "tek_armor_pants_N.uasset",
                          "tek_armor_helmet_N.uasset",
                          "tek_armor_shirt_N.uasset",
                          "tek_armor_shoes_N.uasset",
                          "tek_armor_gloves_N.uasset",
                          "tek_armor_shirt_D.uasset",
                          "Tek_Helmet_LightingMask.uasset",
                          "Tek_Helmet_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Tek", "Colorization"), new[] {
                          "jetpack_Colorize_d.uasset",
                          "jetpack_Colorize_m.uasset",
                          "tek_armor_FPV_Colorize_d.uasset",
                          "tek_armor_FPV_Colorize_m.uasset",
                          "tek_armor_gloves_Colorize_d.uasset",
                          "tek_armor_gloves_Colorize_m.uasset",
                          "tek_armor_helmet_Colorize_d.uasset",
                          "tek_armor_helmet_Colorize_m.uasset",
                          "tek_armor_pants_Colorize_d.uasset",
                          "tek_armor_pants_Colorize_m.uasset",
                          "tek_armor_shirt_Colorize_d.uasset",
                          "tek_armor_shirt_Colorize_m.uasset",
                          "tek_armor_shoes_Colorize_d.uasset",
                          "tek_armor_shoes_Colorize_m.uasset"
                    }}
                },
                ["🦾 Armor - Exo Suit (Gen2)"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "TekGen2"), new[] {
                          "MIC_TekSuitGen2_Colorized.uasset",
                          "MIC_TekSuitGen2_Emissive.uasset",
                          "MIC_TekSuitGen2_Gloves_Colorized.uasset",
                          "MIC_TekSuitGen2_Helmet_Colorized.uasset",
                          "MIC_TekSuitGen2_Jetpack_Colorized.uasset",
                          "MIC_TekSuitGen2_Pants_Colorized.uasset",
                          "MIC_TekSuitGen2_Shirt_Colorized.uasset",
                          "MIC_TekSuitGen2_Shoes_Colorized.uasset",
                          "Tek_Gen2_armor_male_FPV.uasset",
                          "tekGen2_armor_male_gloves.uasset",
                          "tekGen2_armor_male_helmet.uasset",
                          "tekGen2_armor_male_pants.uasset",
                          "tekGen2_armor_male_shirt.uasset",
                          "tekGen2_armor_male_shoes.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "TekGen2", "Textures"), new[] {
                          "T_TekSuitGen2_Gloves_Colorized_D.uasset",
                          "T_TekSuitGen2_Gloves_Colorized_M.uasset",
                          "T_TekSuitGen2_Gloves_N.uasset",
                          "T_TekSuitGen2_Gloves_Layered.uasset",
                          "T_TekSuitGen2_Helmet_Colorized_D.uasset",
                          "T_TekSuitGen2_Helmet_Colorized_M.uasset",
                          "T_TekSuitGen2_Helmet_N.uasset",
                          "T_TekSuitGen2_Helmet_Layered.uasset",
                          "T_TekSuitGen2_Jetpack_Colorized_D.uasset",
                          "T_TekSuitGen2_Jetpack_Colorized_M.uasset",
                          "T_TekSuitGen2_Jetpack_N.uasset",
                          "T_TekSuitGen2_Jetpack_Layered.uasset",
                          "T_TekSuitGen2_Pants_Colorized_D.uasset",
                          "T_TekSuitGen2_Pants_Colorized_M.uasset",
                          "T_TekSuitGen2_Pants_N.uasset",
                          "T_TekSuitGen2_Pants_Layered.uasset",
                          "T_TekSuitGen2_Shirt_Colorized_D.uasset",
                          "T_TekSuitGen2_Shirt_Colorized_M.uasset",
                          "T_TekSuitGen2_Shirt_N.uasset",
                          "T_TekSuitGen2_Shirt_Layered.uasset",
                          "T_TekSuitGen2_Shoes_Colorized_D.uasset",
                          "T_TekSuitGen2_Shoes_Colorized_M.uasset",
                          "T_TekSuitGen2_Shoes_N.uasset"
                    }}
                },
                ["⚙️ Armor - Metal"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Metal", "Colorization"), new[] {
                          "Male_Metal_Boots_Colorize_d.uasset",
                          "Male_Metal_Boots_Colorize_m.uasset",
                          "Male_Metal_Gloves_Colorize_d.uasset",
                          "Male_Metal_Gloves_Colorize_m.uasset",
                          "Male_Metal_Hat_Colorize_d.uasset",
                          "Male_Metal_Hat_Colorize_m.uasset",
                          "Male_Metal_Pants_Colorize_d.uasset",
                          "Male_Metal_Pants_Colorize_m.uasset",
                          "Male_Metal_Shirt_Colorize_d.uasset",
                          "Male_Metal_Shirt_Colorize_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Metal", "Textures"), new[] {
                          "Male_Metal_Boots_Layered.uasset",
                          "Male_Metal_Boots_N.uasset",
                          "Male_Metal_Boots_skin_m.uasset",
                          "Male_Metal_Gloves_D.uasset",
                          "Male_Metal_Gloves_Layered.uasset",
                          "Male_Metal_Gloves_N.uasset",
                          "Male_Metal_Gloves_skin_m.uasset",
                          "Male_Metal_Hat_D.uasset",
                          "Male_Metal_Hat_Layered.uasset",
                          "Male_Metal_Hat_N.uasset",
                          "Male_Metal_Pants_Layered.uasset",
                          "Male_Metal_Pants_N.uasset",
                          "Male_Metal_Pants_skin_m.uasset",
                          "Male_Metal_Shirt_D.uasset",
                          "Male_Metal_Shirt_Layered.uasset",
                          "Male_Metal_Shirt_N.uasset",
                          "Male_Metal_Shirt_skin_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Metal"), new[] {
                          "Male_Metal_Gloves.uasset",
                          "Male_Metal_Hat.uasset",
                          "Male_Metal_Shirt.uasset",
                          "maleMetal_boots.uasset",
                          "maleMetal_Gloves.uasset",
                          "maleMetal_Pants.uasset",
                          "maleMetal_Shirt.uasset"
                    }}

                },
                ["⚙️ Armor - Riot"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Riot", "Textures"), new[] {
                          "Riot_armor_fpv_Layered.uasset",
                          "Riot_armor_fpv_N.uasset",
                          "riot_armor_gloves_Layered.uasset",
                          "riot_armor_gloves_N.uasset",
                          "riot_armor_helmet_D.uasset",
                          "riot_armor_helmet_Layered.uasset",
                          "riot_armor_helmet_N.uasset",
                          "riot_armor_pants_Layered.uasset",
                          "riot_armor_pants_N.uasset",
                          "riot_armor_shirt_Layered.uasset",
                          "riot_armor_shirt_N.uasset",
                          "riot_armor_shoes_Layered.uasset",
                          "riot_armor_shoes_N.uasset",
                          "riot_armor_Male_FPV_gloves_Skin_m.uasset",
                          "riot_armor_Male_gloves_Skin_m.uasset",
                          "riot_armor_Male_pants_Skin_m.uasset",
                          "riot_armor_Male_shirt_skin_m.uasset",
                          "riot_armor_Male_shoes_Skin_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Riot", "Colorization"), new[] {
                          "riot_armor_gloves_Colorize_d.uasset",
                          "riot_armor_gloves_Colorize_m.uasset",
                          "riot_armor_helmet_Colorize_d.uasset",
                          "riot_armor_helmet_Colorize_m.uasset",
                          "riot_armor_pants_Colorize_d.uasset",
                          "riot_armor_pants_Colorize_m.uasset",
                          "riot_armor_shirt_Colorize_d.uasset",
                          "riot_armor_shirt_Colorize_m.uasset",
                          "riot_armor_shoes_Colorize_d.uasset",
                          "riot_armor_shoes_Colorize_m.uasset",
                          "Riot_armor_fpv_Colorize_d.uasset",
                          "Riot_armor_fpv_Colorize_m.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "Human", "Male", "Outfits", "Riot", "Materials"), new[] {
                          "Male_Riot_Colorize_BaseMIC.uasset",
                          "Male_Riot_FPV_Gloves_Colorize_MIC.uasset",
                          "Male_Riot_Gloves_Colorize_MIC.uasset",
                          "Male_Riot_Helmet_Colorize_MIC.uasset",
                          "Male_Riot_Pants_Colorize_MIC.uasset",
                          "Male_Riot_Shirt_Colorize_MIC.uasset",
                          "Male_Riot_Shoes_Colorize_MIC.uasset"
                    }},

                },
                ["🦖 Saddles - Tek"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "TekTier", "tek_rex_saddle"), new[] {
                          "tek_rex_saddle_Colorize_d.uasset",
                          "tek_rex_saddle_Colorize_m.uasset",
                          "tek_rex_saddle_Layered.uasset",
                          "tek_rex_saddle_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "TekTier", "tek_rex_helmet"), new[] {
                          "rex_tek_helmet_Colorize_d.uasset",
                          "rex_tek_helmet_Colorize_m.uasset",
                          "rex_tek_helmet_Layered.uasset",
                          "rex_tek_helmet_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "TekTier", "Tek_Tapejara_Saddle"), new[] {
                          "T_TapejaraTekSaddle_Colorize_d.uasset",
                          "T_TapejaraTekSaddle_Colorize_m.uasset",
                          "T_TapejaraTekSaddle_Layered.uasset",
                          "T_TapejaraTekSaddle_N.uasset"
                    }}
                },
                ["⚡ Tek Weapons"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "TekTier", "TekShield"), new[] {
                          "SM_TekShield.uasset",
                          "T_TekShield_Colorize_m.uasset",
                          "T_TekShield_Colorize_d.uasset",
                          "T_TekShield_D.uasset",
                          "T_TekShield_Layered.uasset",
                          "T_TekShield_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "TekTier", "TekSword"), new[] {
                          "SM_TekSword.uasset",
                          "T_TekSword_BC.uasset",
                          "T_TekSword_Colorize_d.uasset",
                          "T_TekSword_Colorize_m.uasset",
                          "T_TekSword_Layered.uasset",
                          "T_TekSword_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "TekTier", "GigaLaser"), new[] {
                          "SM_GigaLaser.uasset",
                          "T_GigaLaser_D.uasset",
                          "T_GigaLaser_Layered.uasset",
                          "T_GigaLaser_N.uasset"
                    }}
                },
                ["🔫 Turrets (Tek & Heavy)"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "TekTier", "tek_turret"), new[] {
                          "tek_turret_Colorize_d.uasset",
                          "tek_turret_Colorize_m.uasset",
                          "tek_turret_Colorize_m2.uasset",
                          "tek_turret_low_D.uasset",
                          "tek_turret_low_Layered.uasset",
                          "tek_turret_low_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponTurret", "HeavyTurret"), new[] {
                          "HeavyTurret_MIC.uasset",
                          "T_HeavyTurret_D.uasset",
                          "T_HeavyTurret_Layered.uasset",
                          "T_HeavyTurret_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponTurret", "textures"), new[] {
                          "T_AutoTurret_D.uasset",
                          "T_AutoTurret_EmMask.uasset",
                          "T_AutoTurret_Layered.uasset",
                          "T_AutoTurret_N.uasset"
                    }},
                    { Path.Combine(shooterGamePath, "WeaponTurret"), new[] {
                          "AUtoTurret_MIC.uasset",
                          "turret_sm.uasset",
                          "turret_sm_DM.uasset"
                    }}
                },
                ["📦 UI - Inventory Textures"] = new Dictionary<string, string[]>
                {
                    { Path.Combine(shooterGamePath, "UI", "Inventory", "Textures"), Array.Empty<string>() },
                    { Path.Combine(shooterGamePath, "UI", "Inventory", "Textures", "FullBigMain"), Array.Empty<string>() },
                    { Path.Combine(shooterGamePath, "UI", "Inventory", "Textures", "PanelBigMain"), Array.Empty<string>() }
                }
            };
        }

        /// <summary>
        /// Resolves file arrays: if the array is empty, enumerates all .uasset files in the directory.
        /// </summary>
        public static Dictionary<string, string[]> ResolveFiles(Dictionary<string, string[]> folderFiles)
        {
            var resolved = new Dictionary<string, string[]>();
            foreach (var kvp in folderFiles)
            {
                if (kvp.Value.Length == 0 && Directory.Exists(kvp.Key))
                {
                    resolved[kvp.Key] = Directory.GetFiles(kvp.Key, "*.uasset")
                        .Select(Path.GetFileName)
                        .Where(f => f != null)
                        .Select(f => f!)
                        .ToArray();
                }
                else
                {
                    resolved[kvp.Key] = kvp.Value;
                }
            }
            return resolved;
        }
    }
}

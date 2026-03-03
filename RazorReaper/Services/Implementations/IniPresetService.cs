using Microsoft.Extensions.Logging;
using RazorReaper.Models;
using System.Text.Json;

namespace RazorReaper.Services.Implementations;

/// <summary>
/// Implementation of IIniPresetService for managing ARK INI configuration presets.
/// </summary>
public class IniPresetService : IIniPresetService
{
    private readonly ILogger<IniPresetService> _logger;
    private readonly ITelemetryService _telemetryService;
    private readonly List<IniPreset> _presets;
    private readonly List<IniPreset> _customPresets = new();
    private readonly string _customPresetsPath;
    private readonly object _presetLock = new();

    private const string CustomPresetsFileName = "custom-ini-presets.json";

    public IniPresetService(ILogger<IniPresetService> logger, ITelemetryService telemetryService)
    {
        _logger = logger;
        _telemetryService = telemetryService;
        _presets = InitializePresets();
        _customPresetsPath = GetCustomPresetsPath();
        LoadCustomPresets();
    }

    /// <inheritdoc/>
    public List<IniPreset> GetAllPresets()
    {
        try
        {
            _logger.LogDebug("Getting all INI presets");
            lock (_presetLock)
            {
                return new List<IniPreset>(_presets);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all presets");
            return new List<IniPreset>();
        }
    }

    /// <inheritdoc/>
    public IniPreset? GetPresetByName(string name)
    {
        try
        {
            _logger.LogDebug("Getting preset by name: {Name}", name);
            lock (_presetLock)
            {
                return _presets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting preset by name: {Name}", name);
            return null;
        }
    }

    /// <inheritdoc/>
    public string GetPresetImagePath(string presetName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return "/images/presets/default.jpg";
            }

            var imageName = presetName.ToLowerInvariant().Replace(" ", "-");
            var relativePath = $"/images/presets/{imageName}.jpg";
            var physicalPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "images", "presets", $"{imageName}.jpg");

            return File.Exists(physicalPath) ? relativePath : "/images/presets/default.jpg";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting image path for preset: {PresetName}", presetName);
            return "/images/presets/default.jpg";
        }
    }

    /// <inheritdoc/>
    public async Task<bool> AddCustomPresetAsync(IniPreset preset)
    {
        try
        {
            if (!TryNormalizePreset(preset, out var normalized))
            {
                _logger.LogWarning("Custom preset rejected due to invalid data.");
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_add",
                    TelemetryEventStatus.Degraded,
                    "Custom preset rejected due to invalid data.");
                return false;
            }

            normalized.IsCustom = true;

            lock (_presetLock)
            {
                if (_presets.Any(p => p.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogWarning("Preset name already exists: {Name}", normalized.Name);
                    _ = _telemetryService.TrackEventAsync(
                        "ini_preset_add",
                        TelemetryEventStatus.Degraded,
                        "Custom preset name already exists.",
                        new Dictionary<string, object?> { ["preset_name"] = normalized.Name });
                    return false;
                }

                _customPresets.Add(normalized);
                _presets.Add(normalized);
            }

            var saved = await SaveCustomPresetsAsync();
            if (!saved)
            {
                lock (_presetLock)
                {
                    _customPresets.Remove(normalized);
                    _presets.Remove(normalized);
                }
            }

            _ = _telemetryService.TrackEventAsync(
                "ini_preset_add",
                saved ? TelemetryEventStatus.Ok : TelemetryEventStatus.Down,
                saved ? "Custom preset saved." : "Custom preset save failed.",
                new Dictionary<string, object?> { ["preset_name"] = normalized.Name });

            return saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding custom preset");
            _ = _telemetryService.TrackEventAsync(
                "ini_preset_add",
                TelemetryEventStatus.Down,
                ex.Message);
            return false;
        }
    }

    /// <inheritdoc/>
    public async Task<bool> RemoveCustomPresetAsync(string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                _ = _telemetryService.TrackEventAsync(
                    "ini_preset_remove",
                    TelemetryEventStatus.Degraded,
                    "Custom preset remove rejected due to empty name.");
                return false;
            }

            IniPreset? target = null;

            lock (_presetLock)
            {
                target = _customPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (target == null)
                {
                    _ = _telemetryService.TrackEventAsync(
                        "ini_preset_remove",
                        TelemetryEventStatus.Degraded,
                        "Custom preset not found.",
                        new Dictionary<string, object?> { ["preset_name"] = name });
                    return false;
                }

                _customPresets.Remove(target);
                _presets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            var saved = await SaveCustomPresetsAsync();
            if (!saved && target != null)
            {
                lock (_presetLock)
                {
                    _customPresets.Add(target);
                    _presets.Add(target);
                }
            }

            _ = _telemetryService.TrackEventAsync(
                "ini_preset_remove",
                saved ? TelemetryEventStatus.Ok : TelemetryEventStatus.Down,
                saved ? "Custom preset removed." : "Custom preset removal failed.",
                new Dictionary<string, object?> { ["preset_name"] = name });

            return saved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing custom preset");
            _ = _telemetryService.TrackEventAsync(
                "ini_preset_remove",
                TelemetryEventStatus.Down,
                ex.Message,
                new Dictionary<string, object?> { ["preset_name"] = name });
            return false;
        }
    }

    private string GetCustomPresetsPath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "RazorReaper", "Presets", CustomPresetsFileName);
    }

    private void LoadCustomPresets()
    {
        try
        {
            if (!File.Exists(_customPresetsPath))
            {
                return;
            }

            var json = File.ReadAllText(_customPresetsPath);
            var presets = JsonSerializer.Deserialize<List<IniPreset>>(json) ?? new List<IniPreset>();

            lock (_presetLock)
            {
                foreach (var preset in presets)
                {
                    if (!TryNormalizePreset(preset, out var normalized))
                    {
                        continue;
                    }

                    normalized.IsCustom = true;

                    if (_presets.Any(p => p.Name.Equals(normalized.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    _customPresets.Add(normalized);
                    _presets.Add(normalized);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading custom presets");
        }
    }

    private async Task<bool> SaveCustomPresetsAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(_customPresetsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            List<IniPreset> snapshot;
            lock (_presetLock)
            {
                snapshot = new List<IniPreset>(_customPresets);
            }

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_customPresetsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving custom presets");
            return false;
        }
    }

    private static bool TryNormalizePreset(IniPreset? preset, out IniPreset normalized)
    {
        normalized = new IniPreset();

        if (preset == null)
        {
            return false;
        }

        var name = preset.Name?.Trim() ?? string.Empty;
        var content = preset.Content ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        normalized = new IniPreset
        {
            Name = name,
            Description = preset.Description?.Trim() ?? string.Empty,
            Content = content
        };

        return true;
    }

    private List<IniPreset> InitializePresets()
    {
        return new List<IniPreset>
        {
        new IniPreset { Name = "Default", Description = "Standard Config", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=iPad2,IOS
+DeviceProfileNameAndTypes=iPad3,IOS
+DeviceProfileNameAndTypes=iPad4,IOS
+DeviceProfileNameAndTypes=iPadAir,IOS
+DeviceProfileNameAndTypes=iPadMini,IOS
+DeviceProfileNameAndTypes=iPadMini2,IOS
+DeviceProfileNameAndTypes=iPhone4,IOS
+DeviceProfileNameAndTypes=iPhone4S,IOS
+DeviceProfileNameAndTypes=iPhone5,IOS
+DeviceProfileNameAndTypes=iPhone5S,IOS
+DeviceProfileNameAndTypes=iPodTouch5,IOS
+DeviceProfileNameAndTypes=iPhone6,IOS
+DeviceProfileNameAndTypes=iPhone6Plus,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=



[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=
+CVars=r.BloomQuality=0
+CVars=r.DepthOfFieldQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.RefractionQuality=0

[iPad2 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPad3 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPad4 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPadAir DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.BloomQuality=1

[iPadMini DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPadMini2 DeviceProfile]
DeviceType=IOS
BaseProfileName=iPadAir

[iPhone4 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone4S DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone5 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone5S DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=2
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[iPodTouch5 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone6 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=0
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[iPhone6Plus DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=0
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=
+CVars=r.MobileContentScaleFactor=1
+CVars=r.BloomQuality=0
+CVars=r.DepthOfFieldQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.RefractionQuality=0

[Android_Low DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.MobileContentScaleFactor=0.5

[Android_Mid DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.BloomQuality=1
+CVars=r.MobileContentScaleFactor=0.8

[Android_High DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1
+CVars=r.MobileContentScaleFactor=1.0

[Android_Unrecognized DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Mid

[Android_Adreno320 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Mid

;This offset needs to be set for the mosaic fallback to work on Galaxy S4 (SAMSUNG-IGH-I337)
;+CVars=r.DemosaicVposOffset=0.5

[Android_Adreno330 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_High

[Android_Adreno330_Ver53 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Adreno330
+CVars=r.DisjointTimerQueries=1

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=
; we output 10:10:10, not 8:8:8 so we don't need color quantization
+CVars=r.TonemapperQuality=0
; For SSAO we rely on TemporalAA (with a randomized sample pattern over time) so we can use less samples
+CVars=r.AmbientOcclusionSampleSetQuality=0
; less passes, and no upsampling as even upsampling costs some performance
+CVars=r.AmbientOcclusionLevels=1
; larger radius to compensate for fewer passes
+CVars=r.AmbientOcclusionRadiusScale=2


[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=
+CVars=r.RefractionQuality=0

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "Farming", Description = "Gen2 Optimized", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=iPad2,IOS
+DeviceProfileNameAndTypes=iPad3,IOS
+DeviceProfileNameAndTypes=iPad4,IOS
+DeviceProfileNameAndTypes=iPadAir,IOS
+DeviceProfileNameAndTypes=iPadMini,IOS
+DeviceProfileNameAndTypes=iPadMini2,IOS
+DeviceProfileNameAndTypes=iPhone4,IOS
+DeviceProfileNameAndTypes=iPhone4S,IOS
+DeviceProfileNameAndTypes=iPhone5,IOS
+DeviceProfileNameAndTypes=iPhone5S,IOS
+DeviceProfileNameAndTypes=iPodTouch5,IOS
+DeviceProfileNameAndTypes=iPhone6,IOS
+DeviceProfileNameAndTypes=iPhone6Plus,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=ShowFlag.Materials=0
+CVars=FrameRateCap=144
+CVars=FrameRateMinimum=144
+CVars=MaxSmoothedFrameRate=144
+CVars=MinDesiredFrameRate=144
+CVars=NearClipPlane=12.0
+CVars=ShowFlag.MeshEdges=0
+CVars=ShowFlag.MotionBlur=0
+CVars=ShowFlag.AmbientOcclusion=0
+CVars=ShowFlag.AntiAliasing=0
+CVars=ShowFlag.Atmosphere=0
+CVars=ShowFlag.AtmosphericFog=0
+CVars=ShowFlag.AudioRadius=0
+CVars=ShowFlag.BSP=0
+CVars=ShowFlag.BSPSplit=0
+CVars=ShowFlag.BSPTriangles=0
+CVars=ShowFlag.BillboardSprites=0
+CVars=ShowFlag.Brushes=0
+CVars=ShowFlag.BuilderBrush=0
+CVars=ShowFlag.CameraAspectRatioBars=0
+CVars=ShowFlag.CameraFrustums=0
+CVars=ShowFlag.CameraImperfections=0
+CVars=ShowFlag.CameraInterpolation=0
+CVars=ShowFlag.CameraSafeFrames=0
+CVars=ShowFlag.ColorGrading=0
+CVars=ShowFlag.CompositeEditorPrimitives=0
+CVars=ShowFlag.Constraints=0
+CVars=ShowFlag.Cover=0
+CVars=ShowFlag.Decals=0
+CVars=ShowFlag.DeferredLighting=0
+CVars=ShowFlag.DepthOfField=0
+CVars=ShowFlag.Diffuse=0
+CVars=ShowFlag.DirectLighting=0
+CVars=ShowFlag.DirectionalLights=0
+CVars=ShowFlag.DistanceCulledPrimitives=0
+CVars=ShowFlag.DistanceFieldAO=0
+CVars=ShowFlag.DynamicShadows=0
+CVars=ShowFlag.Editor=0
+CVars=ShowFlag.EyeAdaptation=0
+CVars=ShowFlag.Fog=1
+CVars=ShowFlag.Game=0
+CVars=ShowFlag.OnScreenDebug=0
+CVars=ShowFlag.LOD=0
+CVars=ShowFlag.Landscape=0
+CVars=ShowFlag.LargeVertices=0
+CVars=ShowFlag.LensFlares=0
+CVars=ShowFlag.LevelColoration=0
+CVars=ShowFlag.LightComplexity=0
+CVars=ShowFlag.LightInfluences=0
+CVars=ShowFlag.LightMapDensity=0
+CVars=ShowFlag.LightRadius=0
+CVars=ShowFlag.LightShafts=0
+CVars=ShowFlag.Lighting=0
+CVars=ShowFlag.LpvLightingOnly=0
+CVars=ShowFlag.PointLights=0
+CVars=ShowFlag.PostProcessMaterial=0
+CVars=ShowFlag.PostProcessing=0
+CVars=ShowFlag.PrecomputedVisibility=0
+CVars=ShowFlag.PreviewShadowsIndicator=0
+CVars=ShowFlag.ReflectionEnvironment=0
+CVars=ShowFlag.ReflectionOverride=0
+CVars=ShowFlag.Refraction=0
+CVars=ShowFlag.SelectionOutline=0
+CVars=ShowFlag.ShaderComplexity=0
+CVars=ShowFlag.ShadowFrustums=0
+CVars=ShowFlag.ShadowsFromEditorHiddenActors=0
+CVars=ShowFlag.SkeletalMeshes=0
+CVars=ShowFlag.SkyLighting=0
+CVars=ShowFlag.Snap=0
+CVars=ShowFlag.Specular=0
+CVars=ShowFlag.SpotLights=0
+CVars=ShowFlag.StaticMeshes=0
+CVars=ShowFlag.StationaryLightOverlap=0
+CVars=ShowFlag.StereoRendering=0
+CVars=ShowFlag.SubsurfaceScattering=0
+CVars=ShowFlag.TemporalAA=0
+CVars=ShowFlag.Tessellation=0
+CVars=ShowFlag.TestImage=0
+CVars=ShowFlag.TextRender=0
+CVars=ShowFlag.TexturedLightProfiles=0
+CVars=ShowFlag.Tonemapper=0
+CVars=ShowFlag.Translucency=0
+CVars=ShowFlag.VectorFields=0
+CVars=ShowFlag.VertexColors=0
+CVars=ShowFlag.Vignette=0
+CVars=ShowFlag.VisualizeAdaptiveDOF=0
+CVars=ShowFlag.VisualizeBuffer=0
+CVars=ShowFlag.VisualizeDOF=0
+CVars=ShowFlag.VisualizeDistanceFieldAO=0
+CVars=ShowFlag.VisualizeHDR=0
+CVars=ShowFlag.VisualizeLPV=0
+CVars=ShowFlag.VisualizeLightCulling=0
+CVars=ShowFlag.VisualizeMotionBlur=0
+CVars=ShowFlag.VisualizeOutOfBoundsPixels=0
+CVars=ShowFlag.VisualizeSSR=0
+CVars=ShowFlag.VisualizeSenses=0
+CVars=ShowFlag.VolumeLightingSamples=0
+CVars=ShowFlag.Wireframe=0
+CVars=SmoothedFrameRateRange=(LowerBound=(Type=""ERangeBoundTypes::Inclusive"",Value=60),UpperBound=(Type=""ERangeBoundTypes::Exclusive"",Value=70))
+CVars=TEXTUREGROUP_Character=(MinLODSize=1,MaxLODSize=4,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_CharacterNormalMap=(MinLODSize=1,MaxLODSize=4,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_CharacterSpecular=(MinLODSize=1,MaxLODSize=4,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Cinematic=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Effects=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=linear,MipFilter=point)
+CVars=TEXTUREGROUP_EffectsNotFiltered=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Lightmap=(MinLODSize=1,MaxLODSize=8,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_MobileFlattened=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_RenderTarget=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Shadowmap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point,NumStreamedMips=3)
+CVars=TEXTUREGROUP_Skybox=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Terrain_Heightmap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Terrain_Weightmap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_UI=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Vehicle=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_VehicleNormalMap=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_VehicleSpecular=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Weapon=(MinLODSize=1,MaxLODSize=64,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WeaponNormalMap=(MinLODSize=1,MaxLODSize=64,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WeaponSpecular=(MinLODSize=1,MaxLODSize=64,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_World=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WorldNormalMap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WorldSpecular=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=r.DefaultFeature.AmbientOcclusion=False
+CVars=r.DefaultFeature.AntiAliasing=0
+CVars=r.DefaultFeature.AutoExposure=False
+CVars=r.DefaultFeature.Bloom=False
+CVars=r.DefaultFeature.LensFlare=False
+CVars=r.DefaultFeature.MotionBlur=False
+CVars=r.DepthOfFieldQuality=0
+CVars=r.DetailMode=0
+CVars=r.EarlyZPass=0
+CVars=r.ExposureOffset=0.3
+CVars=r.HZBOcclusion=0
+CVars=r.LensFlareQuality=0
+CVars=r.LightFunctionQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.LightShafts=0
+CVars=r.MaxAnisotropy=0
+CVars=r.MotionBlurQuality=0
+CVars=r.PostProcessAAQuality=0
+CVars=r.ReflectionEnvironment=0
+CVars=r.RefractionQuality=0
+CVars=r.SSAOSmartBlur=0
+CVars=r.SSR.Quality=0
+CVars=r.SSS.SampleSet=0
+CVars=r.SSS.Scale=0
+CVars=r.SceneColorFringe.Max=0
+CVars=r.SceneColorFringeQuality=0
+CVars=r.Shadow.CSM.MaxCascades=1
+CVars=r.Shadow.CSM.TransitionScale=0
+CVars=r.Shadow.DistanceScale=0.1
+CVars=r.Shadow.MaxResolution=2
+CVars=r.Shadow.MinResolution=2
+CVars=r.Shadow.RadiusThreshold=0.1
+CVars=r.ShadowQuality=0
+CVars=r.TonemapperQuality=0
+CVars=r.TriangleOrderOptimization=1
+CVars=r.TrueSkyQuality=0
+CVars=r.UpsampleQuality=0
+CVars=r.ViewDistanceScale=0
+CVars=r.oneframethreadlag=1
+CVars=t.maxfps=144

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=
+CVars=r.BloomQuality=0
+CVars=r.DepthOfFieldQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.RefractionQuality=0

[iPad2 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPad3 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPad4 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPadAir DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.BloomQuality=1

[iPadMini DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPadMini2 DeviceProfile]
DeviceType=IOS
BaseProfileName=iPadAir

[iPhone4 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone4S DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone5 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone5S DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=2
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[iPodTouch5 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone6 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=0
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[iPhone6Plus DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=0
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=
+CVars=r.MobileContentScaleFactor=1
+CVars=r.BloomQuality=0
+CVars=r.DepthOfFieldQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.RefractionQuality=0

[Android_Low DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.MobileContentScaleFactor=0.5

[Android_Mid DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.BloomQuality=1
+CVars=r.MobileContentScaleFactor=0.8

[Android_High DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1
+CVars=r.MobileContentScaleFactor=1.0

[Android_Unrecognized DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Mid

[Android_Adreno320 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Mid

;This offset needs to be set for the mosaic fallback to work on Galaxy S4 (SAMSUNG-IGH-I337)
;+CVars=r.DemosaicVposOffset=0.5

[Android_Adreno330 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_High

[Android_Adreno330_Ver53 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Adreno330
+CVars=r.DisjointTimerQueries=1

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=
; we output 10:10:10, not 8:8:8 so we don't need color quantization
+CVars=r.TonemapperQuality=0
; For SSAO we rely on TemporalAA (with a randomized sample pattern over time) so we can use less samples
+CVars=r.AmbientOcclusionSampleSetQuality=0
; less passes, and no upsampling as even upsampling costs some performance
+CVars=r.AmbientOcclusionLevels=1
; larger radius to compensate for fewer passes
+CVars=r.AmbientOcclusionRadiusScale=2


[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=
+CVars=r.RefractionQuality=0

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "Snow North", Description = "Clear Water, 180 FPS", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=foliage.UseOcclusionType=0
+CVars=ShowFloatingDamageText=True
+CVars=FogDensity=0.0
+CVars=FrameRateCap=144
+CVars=FrameRateMinimum=144
+CVars=MaxES2PixelShaderAdditiveComplexityCount=45
+CVars=MaxPixelShaderAdditiveComplexityCount=128
+CVars=MaxSmoothedFrameRate=144
+CVars=MinDesiredFrameRate=75
+CVars=MinSmoothedFrameRate=5
+CVars=NearClipPlane=12.0
+CVars=ShowFlag.Grain=0
+CVars=ShowFlag.Specular=0
+CVars=ShowFlag.AmbientOcclusion=0
+CVars=ShowFlag.AntiAliasing=0
+CVars=ShowFlag.Tessellation=0
+CVars=ShowFlag.SkyLighting=0
+CVars=ShowFlag.Atmosphere=0
+CVars=ShowFlag.AtmosphericFog=0
+CVars=ShowFlag.AudioRadius=0
+CVars=ShowFlag.BSP=0
+CVars=ShowFlag.BSPSplit=0
+CVars=ShowFlag.BSPTriangles=0
+CVars=ShowFlag.BillboardSprites=0
+CVars=ShowFlag.Brushes=0
+CVars=ShowFlag.BuilderBrush=0
+CVars=ShowFlag.CameraAspectRatioBars=0
+CVars=ShowFlag.CameraFrustums=0
+CVars=ShowFlag.CameraImperfections=0
+CVars=ShowFlag.CameraInterpolation=0
+CVars=ShowFlag.CameraSafeFrames=0
+CVars=ShowFlag.CompositeEditorPrimitives=0
+CVars=ShowFlag.Constraints=0
+CVars=ShowFlag.Cover=0
+CVars=ShowFlag.Decals=0
+CVars=ShowFlag.DeferredLighting=0
+CVars=ShowFlag.DepthOfField=0
+CVars=ShowFlag.Diffuse=0
+CVars=ShowFlag.DirectLighting=0
+CVars=ShowFlag.DirectionalLights=0
+CVars=ShowFlag.DistanceCulledPrimitives=0
+CVars=ShowFlag.DistanceFieldAO=0
+CVars=ShowFlag.DynamicShadows=1
+CVars=ShowFlag.Editor=0
+CVars=ShowFlag.Bloom=0
+CVars=ShowFlag.Pivot=0
+CVars=ShowFlag.EyeAdaptation=0
+CVars=ShowFlag.Fog=1
+CVars=ShowFlag.Game=0
+CVars=ShowFlag.LOD=0
+CVars=ShowFlag.Landscape=0
+CVars=ShowFlag.PreviewShadowsIndicator=0
+CVars=ShowFlag.LargeVertices=0
+CVars=ShowFlag.LensFlares=0
+CVars=ShowFlag.LevelColoration=0
+CVars=ShowFlag.Bounds=0
+CVars=ShowFlag.LightComplexity=0
+CVars=ShowFlag.LightInfluences=0
+CVars=ShowFlag.LightMapDensity=0
+CVars=ShowFlag.LightFunctions=1
+CVars=ShowFlag.LightRadius=0
+CVars=ShowFlag.LightShafts=0
+CVars=ShowFlag.Lighting=0
+CVars=ShowFlag.LpvLightingOnly=0
+CVars=ShowFlag.Materials=0
+CVars=ShowFlag.MeshEdges=0
+CVars=ShowFlag.MotionBlur=0
+CVars=ShowFlag.OnScreenDebug=0
+CVars=ShowFlag.OverrideDiffuseAndSpecular=0
+CVars=ShowFlag.Paper2DSprites=0
+CVars=ShowFlag.Particles=0
+CVars=ShowFlag.VisualizeLPV=0
+CVars=t.maxfps=180
+CVars=FX.MaxCPUParticlesPerEmitter=1

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=

[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "Black Super Hard", Description = "Black & Hard Config", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

; === FOG AND ATMOSPHERE SETTINGS ===
+CVars=FogDensity=0.0
+CVars=ShowFlag.AtmosphericFog=0
+CVars=r.Atmosphere=0
+CVars=ShowFlag.Atmosphere=0
+CVars=ShowFlag.Fog=0

; === VISUAL EFFECTS DISABLED ===
+CVars=ShowFlag.Tessellation=0
+CVars=ShowFlag.DepthOfField=0
+CVars=r.DepthOfFieldQuality=0
+CVars=r.BloomQuality=0
+CVars=r.DefaultFeature.Bloom=False
+CVars=r.LensFlareQuality=0
+CVars=r.DefaultFeature.LensFlare=False
+CVars=ShowFlag.LensFlares=0
+CVars=r.MotionBlurQuality=0
+CVars=r.DefaultFeature.MotionBlur=False
+CVars=ShowFlag.MotionBlur=0
+CVars=ShowFlag.SubsurfaceScattering=0
+CVars=r.SSS.Scale=0

; === ANTI-ALIASING SETTINGS ===
+CVars=r.DefaultFeature.AntiAliasing=0
+CVars=r.PostProcessAAQuality=0
+CVars=ShowFlag.AntiAliasing=0
+CVars=ShowFlag.TemporalAA=0

; === AMBIENT OCCLUSION SETTINGS ===
+CVars=r.DefaultFeature.AmbientOcclusion=False
+CVars=r.AmbientOcclusionLevels=0
+CVars=r.AOTrimOldRecordsFraction=0
+CVars=r.SSAOSmartBlur=0
+CVars=ShowFlag.AmbientOcclusion=0
+CVars=ShowFlag.DistanceFieldAO=0
+CVars=r.DistanceFieldAO=0

; === SHADOW SETTINGS ===
+CVars=r.ShadowQuality=0
+CVars=r.Shadow.CSM.MaxCascades=1
+CVars=r.Shadow.CSM.TransitionScale=0
+CVars=r.Shadow.DistanceScale=0.1
+CVars=r.Shadow.MaxResolution=2
+CVars=r.Shadow.MinResolution=2
+CVars=r.Shadow.RadiusThreshold=0.1
+CVars=ShowFlag.DynamicShadows=0
+CVars=ShowFlag.ShadowFrustums=0

; === REFLECTION SETTINGS ===
+CVars=r.ReflectionEnvironment=0
+CVars=r.SSR.Quality=0
+CVars=ShowFlag.ReflectionEnvironment=0
+CVars=ShowFlag.ReflectionOverride=0
+CVars=ShowFlag.VisualizeSSR=0

; === LIGHTING SETTINGS ===
+CVars=ShowFlag.LightFunctions=1
+CVars=r.LightFunctionQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.LightShafts=0
+CVars=ShowFlag.LightShafts=0
+CVars=ShowFlag.DirectLighting=0
+CVars=ShowFlag.DirectionalLights=0
+CVars=ShowFlag.PointLights=0
+CVars=ShowFlag.SpotLights=0
+CVars=ShowFlag.Lighting=0
+CVars=ShowFlag.SkyLighting=0

; === FRAME RATE SETTINGS ===
+CVars=t.maxfps=144
+CVars=FrameRateCap=144
+CVars=FrameRateMinimum=144
+CVars=MaxSmoothedFrameRate=144
+CVars=MinDesiredFrameRate=120
+CVars=MinSmoothedFrameRate=5
+CVars=SmoothedFrameRateRange=(LowerBound=(Type=\""ERangeBoundTypes::Inclusive\"",Value=60),UpperBound=(Type=\""ERangeBoundTypes::Exclusive\"",Value=70))
+CVars=bSmoothFrameRate=true
+CVars=r.oneframethreadlag=1

; === TEXTURE QUALITY SETTINGS ===
; Character Textures
+CVars=TEXTUREGROUP_Character=(MinLODSize=1,MaxLODSize=4,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_CharacterNormalMap=(MinLODSize=1,MaxLODSize=4,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_CharacterSpecular=(MinLODSize=1,MaxLODSize=4,LODBias=0,MinMagFilter=aniso,MipFilter=point)

; World Textures (Very Low Quality)
+CVars=TEXTUREGROUP_World=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WorldNormalMap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WorldSpecular=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)

; Weapon Textures
+CVars=TEXTUREGROUP_Weapon=(MinLODSize=1,MaxLODSize=64,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WeaponNormalMap=(MinLODSize=1,MaxLODSize=64,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_WeaponSpecular=(MinLODSize=1,MaxLODSize=64,LODBias=0,MinMagFilter=aniso,MipFilter=point)

; Vehicle Textures
+CVars=TEXTUREGROUP_Vehicle=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_VehicleNormalMap=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_VehicleSpecular=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)

; Other Texture Groups
+CVars=TEXTUREGROUP_Cinematic=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Effects=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=linear,MipFilter=point)
+CVars=TEXTUREGROUP_EffectsNotFiltered=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Lightmap=(MinLODSize=1,MaxLODSize=8,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Shadowmap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point,NumStreamedMips=3)
+CVars=TEXTUREGROUP_Skybox=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_UI=(MinLODSize=1,MaxLODSize=256,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Terrain_Heightmap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_Terrain_Weightmap=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_MobileFlattened=(MinLODSize=1,MaxLODSize=2,LODBias=0,MinMagFilter=aniso,MipFilter=point)
+CVars=TEXTUREGROUP_RenderTarget=(MinLODSize=1,MaxLODSize=128,LODBias=0,MinMagFilter=aniso,MipFilter=point)

; === RENDERING QUALITY SETTINGS ===
+CVars=r.DetailMode=0
+CVars=r.MaxAnisotropy=0
+CVars=r.ViewDistanceScale=3
+CVars=r.EarlyZPass=0
+CVars=r.HZBOcclusion=0
+CVars=r.TonemapperQuality=0
+CVars=r.UpsampleQuality=0
+CVars=r.TriangleOrderOptimization=1
+CVars=r.TrueSkyQuality=0
+CVars=r.RefractionQuality=0
+CVars=r.ClearWithExcludeRects=0
+CVars=r.CompileShadersForDevelopment=0
+CVars=r.CustomDepth=0

; === POST-PROCESSING SETTINGS ===
+CVars=r.DefaultFeature.AutoExposure=False
+CVars=r.ExposureOffset=0.3
+CVars=r.SceneColorFringe.Max=0
+CVars=r.SceneColorFringeQuality=0
+CVars=ShowFlag.PostProcessing=0
+CVars=ShowFlag.PostProcessMaterial=0
+CVars=ShowFlag.ColorGrading=0
+CVars=ShowFlag.EyeAdaptation=0
+CVars=ShowFlag.Tonemapper=0
+CVars=ShowFlag.Vignette=0

; === PERFORMANCE SETTINGS ===
+CVars=MaxES2PixelShaderAdditiveComplexityCount=45
+CVars=MaxPixelShaderAdditiveComplexityCount=128
+CVars=NearClipPlane=12.0
+CVars=bDisablePhysXHardwareSupport=True
+CVars=bFirstRun=False

; === DEBUG/EDITOR FLAGS DISABLED ===
+CVars=ShowFlag.Pivot=0
+CVars=ShowFlag.LevelColoration=0
+CVars=ShowFlag.LightComplexity=0
+CVars=ShowFlag.Bounds=0
+CVars=ShowFlag.LightMapDensity=0
+CVars=ShowFlag.Materials=0
+CVars=ShowFlag.Brushes=0
+CVars=ShowFlag.GameplayDebug=0
+CVars=ShowFlag.VisualizeLPV=0
+CVars=ShowFlag.AudioRadius=0
+CVars=ShowFlag.BSP=0
+CVars=ShowFlag.BSPSplit=0
+CVars=ShowFlag.BSPTriangles=0
+CVars=ShowFlag.BillboardSprites=0
+CVars=ShowFlag.BuilderBrush=0
+CVars=ShowFlag.CameraAspectRatioBars=0
+CVars=ShowFlag.CameraFrustums=0
+CVars=ShowFlag.CameraImperfections=0
+CVars=ShowFlag.CameraInterpolation=0
+CVars=ShowFlag.CameraSafeFrames=0
+CVars=ShowFlag.CompositeEditorPrimitives=0
+CVars=ShowFlag.Constraints=0
+CVars=ShowFlag.Cover=0
+CVars=ShowFlag.Decals=0
+CVars=ShowFlag.DeferredLighting=0
+CVars=ShowFlag.Diffuse=0
+CVars=ShowFlag.DistanceCulledPrimitives=0
+CVars=ShowFlag.Editor=0
+CVars=ShowFlag.Game=0
+CVars=ShowFlag.LOD=0
+CVars=ShowFlag.Landscape=0
+CVars=ShowFlag.LargeVertices=0
+CVars=ShowFlag.LightInfluences=0
+CVars=ShowFlag.LightRadius=0
+CVars=ShowFlag.LpvLightingOnly=0
+CVars=ShowFlag.MeshEdges=0
+CVars=ShowFlag.OnScreenDebug=0
+CVars=ShowFlag.OverrideDiffuseAndSpecular=0
+CVars=ShowFlag.Paper2DSprites=0
+CVars=ShowFlag.Particles=0
+CVars=ShowFlag.PrecomputedVisibility=0
+CVars=ShowFlag.PreviewShadowsIndicator=0
+CVars=ShowFlag.Refraction=0
+CVars=ShowFlag.SelectionOutline=0
+CVars=ShowFlag.ShaderComplexity=0
+CVars=ShowFlag.ShadowsFromEditorHiddenActors=0
+CVars=ShowFlag.SkeletalMeshes=0
+CVars=ShowFlag.Snap=0
+CVars=ShowFlag.Specular=0
+CVars=ShowFlag.StaticMeshes=0
+CVars=ShowFlag.StationaryLightOverlap=0
+CVars=ShowFlag.StereoRendering=0
+CVars=ShowFlag.TestImage=0
+CVars=ShowFlag.TextRender=0
+CVars=ShowFlag.TexturedLightProfiles=0
+CVars=ShowFlag.Translucency=0
+CVars=ShowFlag.VectorFields=0
+CVars=ShowFlag.VertexColors=0
+CVars=ShowFlag.VisualizeAdaptiveDOF=0
+CVars=ShowFlag.VisualizeBuffer=0
+CVars=ShowFlag.VisualizeDOF=0
+CVars=ShowFlag.VisualizeDistanceFieldAO=0
+CVars=ShowFlag.VisualizeHDR=0
+CVars=ShowFlag.VisualizeLightCulling=0
+CVars=ShowFlag.VisualizeMotionBlur=0
+CVars=ShowFlag.VisualizeOutOfBoundsPixels=0
+CVars=ShowFlag.VisualizeSenses=0
+CVars=ShowFlag.Wireframe=0
+CVars=ShowFlag.SeparateTranslucency=0

; === FOLIAGE SETTINGS ===
+CVars=foliage.UseOcclusionType=0

; === GAMEPLAY SETTINGS ===
+CVars=ShowFloatingDamageText=True

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=

[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "Bloodstalker", Description = "Awesome Spyglass On", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=ShowFlag.AmbientOcclusion=0
+CVars=ShowFlag.AntiAliasing=0
+CVars=ShowFlag.Atmosphere=0
+CVars=ShowFlag.AtmosphericFog=0
+CVars=ShowFlag.AudioRadius=0
+CVars=ShowFlag.BSP=0
+CVars=ShowFlag.BSPSplit=0
+CVars=ShowFlag.BSPTriangles=0
+CVars=ShowFlag.BillboardSprites=0
+CVars=ShowFlag.Brushes=0
+CVars=ShowFlag.BuilderBrush=0
+CVars=ShowFlag.CameraAspectRatioBars=0
+CVars=ShowFlag.CameraFrustums=0
+CVars=ShowFlag.CameraImperfections=0
+CVars=ShowFlag.CameraInterpolation=0
+CVars=ShowFlag.CameraSafeFrames=0
+CVars=ShowFlag.ColorGrading=0
+CVars=ShowFlag.CompositeEditorPrimitives=0
+CVars=ShowFlag.Constraints=0
+CVars=ShowFlag.Cover=0
+CVars=ShowFlag.Decals=0
+CVars=ShowFlag.DeferredLighting=0
+CVars=ShowFlag.DepthOfField=0
+CVars=ShowFlag.Diffuse=0
+CVars=ShowFlag.DirectLighting=0
+CVars=ShowFlag.DirectionalLights=0
+CVars=ShowFlag.DistanceCulledPrimitives=0
+CVars=ShowFlag.DistanceFieldAO=0
+CVars=ShowFlag.DynamicShadows=0
+CVars=ShowFlag.Editor=0
+CVars=ShowFlag.EyeAdaptation=0
+CVars=ShowFlag.Fog=1
+CVars=ShowFlag.Game=0
+CVars=ShowFlag.LOD=0
+CVars=ShowFlag.Landscape=0
+CVars=ShowFlag.LargeVertices=0
+CVars=ShowFlag.LensFlares=0
+CVars=ShowFlag.LevelColoration=0
+CVars=ShowFlag.LightComplexity=0
+CVars=ShowFlag.LightInfluences=0
+CVars=ShowFlag.LightMapDensity=0
+CVars=ShowFlag.LightRadius=0
+CVars=ShowFlag.LightShafts=0
+CVars=ShowFlag.Lighting=0
+CVars=ShowFlag.LpvLightingOnly=0
+CVars=ShowFlag.MeshEdges=0
+CVars=ShowFlag.MotionBlur=0
+CVars=ShowFlag.OnScreenDebug=0
+CVars=ShowFlag.OverrideDiffuseAndSpecular=0
+CVars=ShowFlag.Paper2DSprites=0
+CVars=ShowFlag.Particles=1
+CVars=ShowFlag.Pivot=0
+CVars=ShowFlag.PointLights=0
+CVars=ShowFlag.PostProcessMaterial=0
+CVars=ShowFlag.PostProcessing=0
+CVars=ShowFlag.PrecomputedVisibility=0
+CVars=ShowFlag.PreviewShadowsIndicator=0
+CVars=ShowFlag.ReflectionEnvironment=0
+CVars=ShowFlag.ReflectionOverride=0
+CVars=ShowFlag.Refraction=0
+CVars=ShowFlag.SelectionOutline=0
+CVars=ShowFlag.ShaderComplexity=0
+CVars=ShowFlag.ShadowFrustums=0
+CVars=ShowFlag.ShadowsFromEditorHiddenActors=0
+CVars=ShowFlag.SkeletalMeshes=0
+CVars=ShowFlag.SkyLighting=0
+CVars=ShowFlag.Snap=0
+CVars=ShowFlag.Specular=0
+CVars=ShowFlag.SpotLights=0
+CVars=ShowFlag.StaticMeshes=0
+CVars=ShowFlag.StationaryLightOverlap=0
+CVars=ShowFlag.StereoRendering=0
+CVars=ShowFlag.SubsurfaceScattering=0
+CVars=ShowFlag.TemporalAA=0
+CVars=ShowFlag.Tessellation=0
+CVars=ShowFlag.TestImage=0
+CVars=ShowFlag.TextRender=0
+CVars=ShowFlag.TexturedLightProfiles=0
+CVars=ShowFlag.Tonemapper=0
+CVars=ShowFlag.Translucency=0
+CVars=ShowFlag.VectorFields=0
+CVars=ShowFlag.VertexColors=0
+CVars=ShowFlag.Vignette=0
+CVars=ShowFlag.VisualizeAdaptiveDOF=0
+CVars=ShowFlag.VisualizeBuffer=0
+CVars=ShowFlag.VisualizeDOF=0
+CVars=ShowFlag.VisualizeDistanceFieldAO=0
+CVars=ShowFlag.VisualizeHDR=0
+CVars=ShowFlag.VisualizeLPV=0
+CVars=ShowFlag.VisualizeLightCulling=0
+CVars=ShowFlag.VisualizeMotionBlur=0
+CVars=ShowFlag.VisualizeOutOfBoundsPixels=0
+CVars=ShowFlag.VisualizeSSR=0
+CVars=ShowFlag.VisualizeSenses=0
+CVars=ShowFlag.VolumeLightingSamples=0
+CVars=ShowFlag.Wireframe=0S

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=

[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "Super Hard", Description = "No Visuals, No Water", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=FogDensity=0.0
+CVars=ShowFlag.AtmosphericFog=0
+CVars=ShowFlag.g=0
+CVars=ShowFlag.Game=0
+CVars=ShowFlag.Tessellation=0
+CVars=ShowFlag.DepthOfField=0
+CVars=ShowFlag.Pivot=0
+CVars=ShowFlag.LevelColoration=0
+CVars=ShowFlag.LightComplexity=0
+CVars=ShowFlag.LightFunctions=1
+CVars=ShowFlag.Bounds=0
+CVars=ShowFlag.LightMapDensity=0
+CVars=ShowFlag.Materials=0
+CVars=foliage.UseOcclusionType=0
+CVars=ShowFlag.Brushes=0
+CVars=ShowFlag.GameplayDebug=0

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=

[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "PVP Water", Description = "No Water Surface", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=foliage.UseOcclusionType=0
+CVars=FogDensity=0.0
+CVars=FrameRateCap=300
+CVars=MaxES2PixelShaderAdditiveComplexityCount=45
+CVars=MaxPixelShaderAdditiveComplexityCount=128
+CVars=MaxSmoothedFrameRate=300
+CVars=ShowFlag.Tessellation=0
+CVars=ShowFlag.Materials=0
+CVars=ShowFlag.LightFunctions=1
+CVars=ShowFloatingDamageText=True
+CVars=r.SceneColorFringe.Max=0
+CVars=r.SceneColorFringeQuality=0
+CVars=ShowFlag.GameplayDebug=1
+CVars=FX.MaxCPUParticlesPerEmitter=0
+CVars=t.maxfps=300

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=

[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "PVP Soft", Description = "Standard Pvp Config", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=foliage.UseOcclusionType=0
+CVars=ShowFloatingDamageText=True
+CVars=FogDensity=0.0
+CVars=FrameRateCap=240
+CVars=FrameRateMinimum=240
+CVars=MaxES2PixelShaderAdditiveComplexityCount=45
+CVars=MaxPixelShaderAdditiveComplexityCount=128
+CVars=MaxSmoothedFrameRate=240
+CVars=MinDesiredFrameRate=70
+CVars=MinSmoothedFrameRate=5
+CVars=NearClipPlane=12.0
+CVars=ShowFlag.AmbientOcclusion=0
+CVars=ShowFlag.AntiAliasing=0
+CVars=ShowFlag.Atmosphere=0
+CVars=ShowFlag.AtmosphericFog=0
+CVars=ShowFlag.AudioRadius=0
+CVars=ShowFlag.BSP=0
+CVars=ShowFlag.BSPSplit=0
+CVars=ShowFlag.BSPTriangles=0
+CVars=ShowFlag.BillboardSprites=0
+CVars=ShowFlag.Brushes=0
+CVars=ShowFlag.BuilderBrush=0
+CVars=ShowFlag.CameraAspectRatioBars=0
+CVars=ShowFlag.CameraFrustums=0
+CVars=ShowFlag.CameraImperfections=0
+CVars=ShowFlag.CameraInterpolation=0
+CVars=ShowFlag.CameraSafeFrames=0
+CVars=ShowFlag.CompositeEditorPrimitives=0
+CVars=ShowFlag.Constraints=0
+CVars=ShowFlag.Cover=0
+CVars=ShowFlag.Decals=0
+CVars=ShowFlag.DeferredLighting=0
+CVars=ShowFlag.DepthOfField=0
+CVars=ShowFlag.Diffuse=0
+CVars=ShowFlag.DirectLighting=0
+CVars=ShowFlag.DirectionalLights=0
+CVars=ShowFlag.DynamicShadows=1
+CVars=ShowFlag.DistanceCulledPrimitives=0
+CVars=ShowFlag.DistanceFieldAO=0
+CVars=ShowFlag.DynamicShadows=0
+CVars=ShowFlag.Editor=0
+CVars=ShowFlag.EyeAdaptation=0
+CVars=ShowFlag.Fog=1
+CVars=ShowFlag.Game=0
+CVars=ShowFlag.LOD=0
+CVars=ShowFlag.Landscape=0
+CVars=ShowFlag.LargeVertices=0
+CVars=ShowFlag.LensFlares=0
+CVars=ShowFlag.LevelColoration=0
+CVars=ShowFlag.LightComplexity=0
+CVars=ShowFlag.LightInfluences=0
+CVars=ShowFlag.LightMapDensity=0
+CVars=ShowFlag.LightRadius=0
+CVars=ShowFlag.LightShafts=0
+CVars=ShowFlag.Lighting=0
+CVars=ShowFlag.LpvLightingOnly=0
+CVars=ShowFlag.Materials=0
+CVars=ShowFlag.MeshEdges=0
+CVars=ShowFlag.MotionBlur=0
+CVars=ShowFlag.OnScreenDebug=0
+CVars=ShowFlag.OverrideDiffuseAndSpecular=0
+CVars=ShowFlag.Paper2DSprites=0
+CVars=ShowFlag.Particles=0
+CVars=ShowFlag
+CVars=FX.MaxCPUParticlesPerEmitter=1
+CVars=t.maxfps=360

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=

[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "PVP Hard", Description = "Minimal Visuals, Fogless", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=foliage.UseOcclusionType=0
+CVars=ShowFloatingDamageText=True
+CVars=FogDensity=0.0
+CVars=FrameRateCap=180
+CVars=FrameRateMinimum=180
+CVars=MaxES2PixelShaderAdditiveComplexityCount=45
+CVars=MaxPixelShaderAdditiveComplexityCount=128
+CVars=MaxSmoothedFrameRate=180
+CVars=MinDesiredFrameRate=80
+CVars=MinSmoothedFrameRate=5
+CVars=NearClipPlane=12.5
+CVars=ShowFlag.AmbientOcclusion=0
+CVars=ShowFlag.AntiAliasing=0
+CVars=ShowFlag.Atmosphere=0
+CVars=ShowFlag.AtmosphericFog=0
+CVars=ShowFlag.AudioRadius=0
+CVars=ShowFlag.BSP=0
+CVars=ShowFlag.BSPSplit=0
+CVars=ShowFlag.BSPTriangles=0
+CVars=ShowFlag.BillboardSprites=0
+CVars=ShowFlag.Brushes=0
+CVars=ShowFlag.BuilderBrush=0
+CVars=ShowFlag.CameraAspectRatioBars=0
+CVars=ShowFlag.CameraFrustums=0
+CVars=ShowFlag.CameraImperfections=0
+CVars=ShowFlag.CameraInterpolation=0
+CVars=ShowFlag.CameraSafeFrames=0
+CVars=ShowFlag.CompositeEditorPrimitives=0
+CVars=ShowFlag.Constraints=0
+CVars=ShowFlag.Cover=0
+CVars=ShowFlag.Decals=0
+CVars=ShowFlag.DeferredLighting=0
+CVars=ShowFlag.DepthOfField=0
+CVars=ShowFlag.Diffuse=0
+CVars=ShowFlag.DirectLighting=0
+CVars=ShowFlag.DirectionalLights=0
+CVars=ShowFlag.DistanceCulledPrimitives=0
+CVars=ShowFlag.DistanceFieldAO=0
+CVars=ShowFlag.DynamicShadows=0
+CVars=ShowFlag.Editor=0
+CVars=ShowFlag.EyeAdaptation=0
+CVars=ShowFlag.Fog=1
+CVars=ShowFlag.Game=0
+CVars=ShowFlag.LOD=0
+CVars=ShowFlag.Landscape=0
+CVars=ShowFlag.LargeVertices=0
+CVars=ShowFlag.LensFlares=0
+CVars=ShowFlag.LevelColoration=0
+CVars=ShowFlag.LightComplexity=0
+CVars=ShowFlag.LightInfluences=0
+CVars=ShowFlag.LightMapDensity=0
+CVars=ShowFlag.LightRadius=0
+CVars=ShowFlag.LightShafts=0
+CVars=ShowFlag.Lighting=0
+CVars=FX.MaxCPUParticlesPerEmitter=0
+CVars=ShowFlag.LpvLightingOnly=0
+CVars=ShowFlag.Materials=0
+CVars=ShowFlag.MeshEdges=0
+CVars=ShowFlag.MotionBlur=0
+CVars=ShowFlag.OnScreenDebug=0
+CVars=ShowFlag.OverrideDiffuseAndSpecular=0
+CVars=ShowFlag.Paper2DSprites=0
+CVars=ShowFlag.Particles=0
+CVars=ShowFlag
+CVars=r.SceneColorFringe.Max=0
+CVars=ShowFlag.DynamicShadows=1
+CVars=ShowFlag.LightComplexity=0
+CVars=r.oneframethreadlag=1
+CVars=t.maxfps=

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=

[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" },
        new IniPreset { Name = "Creator Config", Description = "Best For Content", Content = @"[DeviceProfiles]
+DeviceProfileNameAndTypes=Windows,Windows
+DeviceProfileNameAndTypes=WindowsNoEditor,WindowsNoEditor
+DeviceProfileNameAndTypes=WindowsServer,WindowsServer
+DeviceProfileNameAndTypes=IOS,IOS
+DeviceProfileNameAndTypes=iPad2,IOS
+DeviceProfileNameAndTypes=iPad3,IOS
+DeviceProfileNameAndTypes=iPad4,IOS
+DeviceProfileNameAndTypes=iPadAir,IOS
+DeviceProfileNameAndTypes=iPadMini,IOS
+DeviceProfileNameAndTypes=iPadMini2,IOS
+DeviceProfileNameAndTypes=iPhone4,IOS
+DeviceProfileNameAndTypes=iPhone4S,IOS
+DeviceProfileNameAndTypes=iPhone5,IOS
+DeviceProfileNameAndTypes=iPhone5S,IOS
+DeviceProfileNameAndTypes=iPodTouch5,IOS
+DeviceProfileNameAndTypes=iPhone6,IOS
+DeviceProfileNameAndTypes=iPhone6Plus,IOS
+DeviceProfileNameAndTypes=Android,Android
+DeviceProfileNameAndTypes=PS4,PS4
+DeviceProfileNameAndTypes=XboxOne,XboxOne
+DeviceProfileNameAndTypes=HTML5,HTML5
+DeviceProfileNameAndTypes=Mac,Mac
+DeviceProfileNameAndTypes=MacNoEditor,MacNoEditor
+DeviceProfileNameAndTypes=MacServer,MacServer
+DeviceProfileNameAndTypes=WinRT,WinRT
+DeviceProfileNameAndTypes=Linux,Linux
+DeviceProfileNameAndTypes=LinuxNoEditor,LinuxNoEditor
+DeviceProfileNameAndTypes=LinuxServer,LinuxServer

[Windows DeviceProfile]
DeviceType=Windows
BaseProfileName=

+CVars=foliage.UseOcclusionType=0
+CVars=FogDensity=0.0
+CVars=FrameRateCap=300
+CVars=MaxES2PixelShaderAdditiveComplexityCount=45
+CVars=MaxPixelShaderAdditiveComplexityCount=128
+CVars=MaxSmoothedFrameRate=300
+CVars=ShowFlag.Tessellation=0
+CVars=ShowFlag.Materials=0
+CVars=ShowFlag.LightFunctions=1
+CVars=ShowFloatingDamageText=True
+CVars=r.SceneColorFringe.Max=0
+CVars=r.SceneColorFringeQuality=0
+CVars=ShowFlag.GameplayDebug=1
+CVars=FX.MaxCPUParticlesPerEmitter=0
+CVars=t.maxfps=300

[WindowsNoEditor DeviceProfile]
DeviceType=WindowsNoEditor
BaseProfileName=Windows

[WindowsServer DeviceProfile]
DeviceType=WindowsServer
BaseProfileName=Windows

[IOS DeviceProfile]
DeviceType=IOS
BaseProfileName=
+CVars=r.BloomQuality=0
+CVars=r.DepthOfFieldQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.RefractionQuality=0

[iPad2 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPad3 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPad4 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPadAir DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.BloomQuality=1

[iPadMini DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPadMini2 DeviceProfile]
DeviceType=IOS
BaseProfileName=iPadAir

[iPhone4 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone4S DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone5 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone5S DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=2
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[iPodTouch5 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.RenderTargetSwitchWorkaround=1

[iPhone6 DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=0
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[iPhone6Plus DeviceProfile]
DeviceType=IOS
BaseProfileName=IOS
+CVars=r.MobileContentScaleFactor=0
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1

[Android DeviceProfile]
DeviceType=Android
BaseProfileName=
+CVars=r.MobileContentScaleFactor=1
+CVars=r.BloomQuality=0
+CVars=r.DepthOfFieldQuality=0
+CVars=r.LightShaftQuality=0
+CVars=r.RefractionQuality=0

[Android_Low DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.MobileContentScaleFactor=0.5

[Android_Mid DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.BloomQuality=1
+CVars=r.MobileContentScaleFactor=0.8

[Android_High DeviceProfile]
DeviceType=Android
BaseProfileName=Android
+CVars=r.BloomQuality=1
+CVars=r.DepthOfFieldQuality=1
+CVars=r.LightShaftQuality=1
+CVars=r.MobileContentScaleFactor=1.0

[Android_Unrecognized DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Mid

[Android_Adreno320 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Mid

;This offset needs to be set for the mosaic fallback to work on Galaxy S4 (SAMSUNG-IGH-I337)
;+CVars=r.DemosaicVposOffset=0.5

[Android_Adreno330 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_High

[Android_Adreno330_Ver53 DeviceProfile]
DeviceType=Android
BaseProfileName=Android_Adreno330
+CVars=r.DisjointTimerQueries=1

[PS4 DeviceProfile]
DeviceType=PS4
BaseProfileName=

[XboxOne DeviceProfile]
DeviceType=XboxOne
BaseProfileName=
; we output 10:10:10, not 8:8:8 so we don't need color quantization
+CVars=r.TonemapperQuality=0
; For SSAO we rely on TemporalAA (with a randomized sample pattern over time) so we can use less samples
+CVars=r.AmbientOcclusionSampleSetQuality=0
; less passes, and no upsampling as even upsampling costs some performance
+CVars=r.AmbientOcclusionLevels=1
; larger radius to compensate for fewer passes
+CVars=r.AmbientOcclusionRadiusScale=2


[HTML5 DeviceProfile]
DeviceType=HTML5
BaseProfileName=
+CVars=r.RefractionQuality=0

[Mac DeviceProfile]
DeviceType=Mac
BaseProfileName=

[MacNoEditor DeviceProfile]
DeviceType=MacNoEditor
BaseProfileName=Mac

[MacServer DeviceProfile]
DeviceType=MacServer
BaseProfileName=Mac

[WinRT DeviceProfile]
DeviceType=WinRT
BaseProfileName=

[Linux DeviceProfile]
DeviceType=Linux
BaseProfileName=
MeshLODSettings=
TextureLODSettings=

[LinuxNoEditor DeviceProfile]
DeviceType=LinuxNoEditor
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=

[LinuxServer DeviceProfile]
DeviceType=LinuxServer
BaseProfileName=Linux
MeshLODSettings=
TextureLODSettings=" }
        };
    }
}

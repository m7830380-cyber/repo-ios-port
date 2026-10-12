using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

// Builds the iOS player "shell" with Unity 2022.3.67f2 (the game's exact version): an IL2CPP player that
// contains the patched retail R.E.P.O. assemblies (Assets/Plugins/Game, from the private repo) plus the iOS
// mod. The game's own data files are swapped into the .app afterwards by tools/package_ipa.py.
public static class IosShellBuild
{
    const string ScenePath = "Assets/Scenes/Boot.unity";

    public static void Build()
    {
        string outDir = Environment.GetEnvironmentVariable("SHELL_OUT") ?? "Build/iOS";

        if (!File.Exists(ScenePath))
        {
            Directory.CreateDirectory("Assets/Scenes");
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            EditorSceneManager.SaveScene(scene, ScenePath);
        }

        EditorUserBuildSettings.SwitchActiveBuildTarget(BuildTargetGroup.iOS, BuildTarget.iOS);

        PlayerSettings.companyName = "semiwork";
        PlayerSettings.productName = "REPO";
        PlayerSettings.SetApplicationIdentifier(BuildTargetGroup.iOS, "com.semiwork.repo.ios");
        PlayerSettings.bundleVersion = "1.0";
        PlayerSettings.iOS.buildNumber = Environment.GetEnvironmentVariable("GITHUB_RUN_NUMBER") ?? "1";
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.iOS, ScriptingImplementation.IL2CPP);
        PlayerSettings.SetArchitecture(BuildTargetGroup.iOS, 1); // ARM64
        // link.xml keeps every game assembly whole; Minimal is the least stripping 2022.3 allows.
        PlayerSettings.SetManagedStrippingLevel(BuildTargetGroup.iOS, ManagedStrippingLevel.Minimal);
        PlayerSettings.stripEngineCode = false;
        PlayerSettings.SetIl2CppCompilerConfiguration(BuildTargetGroup.iOS, Il2CppCompilerConfiguration.Release);
        PlayerSettings.SetIl2CppCodeGeneration(NamedBuildTarget.iOS, Il2CppCodeGeneration.OptimizeSpeed);
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.iOS, ApiCompatibilityLevel.NET_Unity_4_8);
        PlayerSettings.allowUnsafeCode = true;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.iOS, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS, new[] { GraphicsDeviceType.Metal });
        PlayerSettings.colorSpace = ColorSpace.Gamma;       // same as the retail game
        PlayerSettings.gpuSkinning = true;                  // same as the retail game
        PlayerSettings.MTRendering = true;
        PlayerSettings.iOS.targetOSVersionString = "14.0";
        PlayerSettings.iOS.sdkVersion = iOSSdkVersion.DeviceSDK;
        PlayerSettings.iOS.targetDevice = iOSTargetDevice.iPhoneAndiPad;
        PlayerSettings.iOS.requiresFullScreen = true;
        PlayerSettings.iOS.hideHomeButton = true;
        PlayerSettings.iOS.deferSystemGesturesMode = UnityEngine.iOS.SystemGestureDeferMode.All;
        PlayerSettings.iOS.statusBarStyle = iOSStatusBarStyle.Default;
        PlayerSettings.statusBarHidden = true;
        PlayerSettings.iOS.appInBackgroundBehavior = iOSAppInBackgroundBehavior.Suspend;
        PlayerSettings.iOS.forceHardShadowsOnMetal = false;
        PlayerSettings.accelerometerFrequency = 0;
        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.SplashScreen.show = false;
        PlayerSettings.SplashScreen.showUnityLogo = false;
        PlayerSettings.enableInternalProfiler = false;
        PlayerSettings.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Warning, StackTraceLogType.None);
        PlayerSettings.SetStackTraceLogType(LogType.Error, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Assert, StackTraceLogType.ScriptOnly);
        PlayerSettings.SetStackTraceLogType(LogType.Exception, StackTraceLogType.ScriptOnly);
        PlayerSettings.usePlayerLog = true;
        PlayerSettings.iOS.appleEnableAutomaticSigning = false;
        SetProjectSetting("activeInputHandler", 2);         // both input systems, like the retail game
        // The retail game's PlayerSettings (in its globalgamemanagers) replace most of these at runtime,
        // but the shell must agree on anything baked into the player binary.

        var opts = new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = outDir,
            target = BuildTarget.iOS,
            targetGroup = BuildTargetGroup.iOS,
            options = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log("[IosShellBuild] result: " + report.summary.result + " errors=" + report.summary.totalErrors);
        if (report.summary.result != UnityEditor.Build.Reporting.BuildResult.Succeeded)
        {
            // Keep the linked assemblies IL2CPP choked on (Temp is wiped when the editor quits) so CI can
            // hand them to the private repo for inspection. They contain game code: never publish them here.
            string dbg = Environment.GetEnvironmentVariable("SHELL_DEBUG_OUT");
            if (!string.IsNullOrEmpty(dbg) && Directory.Exists("Temp/StagingArea/Data/Managed"))
            {
                Directory.CreateDirectory(dbg);
                foreach (var f in Directory.GetFiles("Temp/StagingArea/Data/Managed", "*.dll"))
                    File.Copy(f, Path.Combine(dbg, Path.GetFileName(f)), true);
                Debug.Log("[IosShellBuild] copied staged assemblies to " + dbg);
            }
            EditorApplication.Exit(1);
        }
    }

    static void SetProjectSetting(string name, int value)
    {
        var asset = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset").FirstOrDefault();
        if (asset == null) { Debug.LogWarning("[IosShellBuild] ProjectSettings.asset not found"); return; }
        var so = new SerializedObject(asset);
        var p = so.FindProperty(name);
        if (p == null) { Debug.LogWarning("[IosShellBuild] project setting missing: " + name); return; }
        p.intValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
        Debug.Log($"[IosShellBuild] {name} = {value}");
    }

    [PostProcessBuild(100)]
    public static void OnPostprocess(BuildTarget target, string path)
    {
        if (target != BuildTarget.iOS) return;

        string plistPath = Path.Combine(path, "Info.plist");
        var plist = new PlistDocument();
        plist.ReadFromFile(plistPath);
        var root = plist.root;
        root.SetString("CFBundleDisplayName", "R.E.P.O.");
        root.SetBoolean("UIFileSharingEnabled", true);           // Documents visible in Files / Finder
        root.SetBoolean("LSSupportsOpeningDocumentsInPlace", true);
        root.SetBoolean("UISupportsDocumentBrowser", true);
        root.SetBoolean("GCSupportsControllerUserInteraction", true);
        root.SetBoolean("GCSupportsGameMode", true);
        root.SetBoolean("CADisableMinimumFrameDurationOnPhone", true); // allow 120Hz on ProMotion
        root.SetBoolean("UIRequiresFullScreen", true);
        root.SetBoolean("UIStatusBarHidden", true);
        root.SetBoolean("ITSAppUsesNonExemptEncryption", false);
        if (root.values.ContainsKey("NSMicrophoneUsageDescription"))
            root.values.Remove("NSMicrophoneUsageDescription");      // voice chat is disabled on iOS
        var gcProfiles = root.CreateArray("GCSupportedGameControllers");
        gcProfiles.AddDict().SetString("ProfileName", "ExtendedGamepad");
        plist.WriteToFile(plistPath);

        // Unsigned build (TrollStore install; CI fakesigns with ldid + entitlements afterwards).
        string projPath = PBXProject.GetPBXProjectPath(path);
        var proj = new PBXProject();
        proj.ReadFromFile(projPath);
        foreach (var guid in new[] { proj.GetUnityMainTargetGuid(), proj.GetUnityFrameworkTargetGuid() })
        {
            proj.SetBuildProperty(guid, "CODE_SIGNING_ALLOWED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGNING_REQUIRED", "NO");
            proj.SetBuildProperty(guid, "CODE_SIGN_IDENTITY", "");
            proj.SetBuildProperty(guid, "ENABLE_BITCODE", "NO");
            proj.SetBuildProperty(guid, "IPHONEOS_DEPLOYMENT_TARGET", "14.0");
            proj.SetBuildProperty(guid, "GCC_TREAT_WARNINGS_AS_ERRORS", "NO");
        }
        proj.WriteToFile(projPath);
    }
}

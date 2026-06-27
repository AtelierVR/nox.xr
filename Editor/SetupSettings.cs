using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace Nox.XR.Editor
{
    public class SetupSettings : IEditorModInitializer
    {
        public void OnInitializeEditor(IEditorModCoreAPI api)
        {
            ApplyXRStartupSettings();
            EnableViveTrackerProfile();
        }

        public void OnDisposeEditor() { }

        /// <summary>
        /// Disables "Initialize XR on Startup" for all build targets except Android XR and Vision OS,
        /// which are the only platforms that should auto-initialize XR at runtime.
        /// </summary>
        private static void ApplyXRStartupSettings()
        {
            bool dirty = false;

            foreach (BuildTargetGroup group in System.Enum.GetValues(typeof(BuildTargetGroup)))
            {
                if (group == BuildTargetGroup.Unknown)
                    continue;

                var settings = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
                if (settings == null)
                    continue;

                bool shouldEnable = group == BuildTargetGroup.Android
                    || group == BuildTargetGroup.VisionOS;

                if (settings.InitManagerOnStart == shouldEnable)
                    continue;

                settings.InitManagerOnStart = shouldEnable;
                EditorUtility.SetDirty(settings);
                dirty = true;
            }

            if (dirty)
                AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// Enables the HTC Vive Tracker OpenXR interaction profile for Standalone,
        /// so that Vive Trackers are detected without the SteamVR plugin.
        /// </summary>
        private static void EnableViveTrackerProfile()
        {
            var settings = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Standalone);
            if (settings == null)
                return;

            var feature = settings.GetFeature<HTCViveTrackerProfile>();
            if (feature?.enabled != false)
                return;

            feature.enabled = true;
            EditorUtility.SetDirty(settings);
            AssetDatabase.SaveAssets();
        }
    }
}

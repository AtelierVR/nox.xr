using Nox.CCK.Mods.Cores;
using Nox.CCK.Mods.Initializers;
using UnityEditor;
using UnityEditor.XR.Management;
using UnityEngine.XR.Management;

namespace Nox.XR.Editor
{
    public class SetupSettings : IEditorModInitializer
    {
        public void OnInitializeEditor(IEditorModCoreAPI api)
            => ApplyXRStartupSettings();

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
    }
}

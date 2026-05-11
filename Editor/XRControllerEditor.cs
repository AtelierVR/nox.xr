#if UNITY_EDITOR
using Nox.XR;
using UnityEditor;
using UnityEngine;

namespace Nox.XR.Editor {
	[CustomEditor(typeof(XRController))]
	public class XRProxyEditor : UnityEditor.Editor {

		[MenuItem("Nox/XR/Open XR Settings")]
		public static void OpenXRSettings()
			=> SettingsService.OpenProjectSettings("Project/XR Plug-in Management");
		
		private const string XRMenuPath = "Nox/XR/Enable XR";

		[MenuItem(XRMenuPath, false)]
		public static void ToggleVR() {
			XRController.NoVRFlag = !XRController.NoVRFlag;
		}

		[MenuItem(XRMenuPath, true)]
		private static bool ToggleVRValidate() {
			Menu.SetChecked(XRMenuPath, !XRController.NoVRFlag);
			return true;
		}

		public override void OnInspectorGUI() {
			base.OnInspectorGUI();
			
			var controller = (XRController)target;
			if (!controller) {
				EditorGUILayout.LabelField("Controller is null");
				return;
			}

			var abilities = controller.GetAbilities();
			if (abilities == null || abilities.Count == 0) {
				EditorGUILayout.LabelField("No abilities found");
			} else {
				EditorGUILayout.LabelField($"Abilities ({abilities.Count})");
				foreach (var ability in abilities)
					EditorGUILayout.TextField(
						$" - {ability.Key}",
						ability.Value.ToString()
					);
			}

			EditorGUILayout.Space();

			EditorGUILayout.ObjectField(controller.GetAvatar()?.Descriptor.Anchor, typeof(GameObject), true);
		}

		public override bool RequiresConstantRepaint() {
			return true;
		}
	}
}
#endif
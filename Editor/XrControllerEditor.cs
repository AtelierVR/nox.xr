#if UNITY_EDITOR
using api.nox.xr;
using UnityEditor;
using UnityEngine;

namespace Mods.api.nox.xr.editor {
	[CustomEditor(typeof(XRController))]
	public class XRProxyEditor : Editor {
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
			
			EditorGUILayout.ObjectField(controller.GetAvatar()?.GetDescriptor().GetAnchor(), typeof(GameObject), true);
		}
		
		public override bool RequiresConstantRepaint() {
			return true;
		}
	}
}
#endif
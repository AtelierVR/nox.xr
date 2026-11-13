#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Nox.XR.Editor {
	/// <summary>
	/// Editor helpers for the v2 XR controller.
	/// <para>
	/// The v2 proxy lives in its own prefab (<c>Packages/nox.xr/Assets/xr/controller.prefab</c>) which
	/// contains nothing but <c>XRControllerV2</c> and a <c>Player</c> prefab instance: every reference is
	/// resolved at runtime, so the prefab can be dropped anywhere.
	/// </para>
	/// </summary>
	public static class XRControllerV2Editor {
		private const string V2PrefabPath = "Packages/nox.xr/Assets/xr/controller.prefab";

		// Action range of the "Nox/XR" submenu, see XRControllerEditor (actions: 1007-1009).
		private const int SpawnPriority = 1008;

		[MenuItem("Nox/XR/Spawn Controller (V2)", false, SpawnPriority)]
		public static void Spawn() {
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(V2PrefabPath);
			if (!prefab) {
				Debug.LogError($"[XRControllerV2] Prefab not found: {V2PrefabPath}");
				return;
			}

			var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
			Undo.RegisterCreatedObjectUndo(instance, "Spawn XR Controller V2");
			Selection.activeGameObject = instance;
		}

		[MenuItem("Nox/XR/Spawn Controller (V2)", true)]
		private static bool SpawnValidate()
			=> AssetDatabase.LoadAssetAtPath<GameObject>(V2PrefabPath);

		[MenuItem("Nox/XR/Select Controller Prefab (V2)", false, SpawnPriority + 1)]
		public static void SelectPrefab() {
			var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(V2PrefabPath);
			if (!prefab)
				return;

			Selection.activeObject = prefab;
			EditorGUIUtility.PingObject(prefab);
		}
	}
}
#endif

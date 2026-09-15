#if UNITY_EDITOR
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Editor {
	[CustomEditor(typeof(XRController))]
	public class XRProxyEditor : UnityEditor.Editor {

		// La position d'un sous-menu dans "Nox/" est dérivée des priorités de ses enfants.
		// Pour rester collé aux autres sous-menus Nox (priorité par défaut 1000), TOUTES
		// les priorités de "Nox/XR" doivent tenir dans [990, 1010] - y compris celles de
		// XROpenXRDebugTools. Les infos/diagnostics occupent 991-996, l'écart de 11 (> 10)
		// avec les actions (1007-1009) crée le séparateur.
		private const int XRSettingsPriority = 991;  // Open XR Settings
		private const int XRActionPriority   = 1007; // Enable XR, Enter XR, Leave XR

		[MenuItem("Nox/XR/Open XR Settings", false, XRSettingsPriority)]
		public static void OpenXRSettings()
			=> SettingsService.OpenProjectSettings("Project/XR Plug-in Management");

		private const string XRMenuPath      = "Nox/XR/Enable XR";
		private const string EnterXRMenuPath = "Nox/XR/Enter XR";
		private const string LeaveXRMenuPath = "Nox/XR/Leave XR";

		/// <summary>
		/// Toggle persisté : détermine si la XR est armée au démarrage.
		/// </summary>
		[MenuItem(XRMenuPath, false, XRActionPriority)]
		public static void ToggleVR() 
			=> Settings.EnableXRSetting.Value = !Settings.EnableXRSetting.Value;
		

		[MenuItem(XRMenuPath, true)]
		private static bool ToggleVRValidate() {
			Menu.SetChecked(XRMenuPath, Settings.EnableXRSetting.Value);
			return true;
		}

		/// <summary>
		/// Entrer en XR. En Play mode, démarre réellement le loader ; hors Play mode, arme
		/// simplement le démarrage automatique (le loader ne peut pas être piloté hors jeu).
		/// </summary>
		[MenuItem(EnterXRMenuPath, false, XRActionPriority + 1)]
		public static void EnterXR()
			=> SetXRRunning(true);

		/// <summary>
		/// Quitter la XR. En Play mode, arrête le loader ET retire le proxy XR pour retomber
		/// sur un autre contrôleur ; hors Play mode, désarme le démarrage automatique.
		/// </summary>
		[MenuItem(LeaveXRMenuPath, false, XRActionPriority + 2)]
		public static void LeaveXR()
			=> SetXRRunning(false);

		[MenuItem(EnterXRMenuPath, true)]
		private static bool EnterXRValidate() {
			// En jeu : seulement si on n'y est pas. Hors jeu : seulement si ce n'est pas déjà armé.
			return Application.isPlaying
				? !IsXRRunning()
				: !Settings.EnableXRSetting.Value;
		}

		[MenuItem(LeaveXRMenuPath, true)]
		private static bool LeaveXRValidate() {
			// En jeu : seulement si on y est. Hors jeu : seulement si c'est armé.
			return Application.isPlaying
				? IsXRRunning()
				: Settings.EnableXRSetting.Value;
		}

		private static bool IsXRRunning() {
			var client = Client.Instance;
			return Application.isPlaying && client != null && client.IsXRInitialized();
		}

		private static void SetXRRunning(bool running) {
			if (!Application.isPlaying) {
				// Hors Play mode le loader ne peut pas être piloté : on arme/désarme le
				// démarrage automatique pour le prochain lancement.
				Settings.EnableXRSetting.Value = running;
				Logger.Log(running
					? "XR will start on the next Play (startup flag armed)."
					: "XR will not start on the next Play (startup flag cleared).");
				return;
			}

			var client = Client.Instance;
			if (client == null) {
				Logger.LogWarning("XR client is not available (is the nox.xr mod loaded?).");
				return;
			}

			if (running)
				client.EnterXR().Forget();
			else
				client.QuitXR().Forget();
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
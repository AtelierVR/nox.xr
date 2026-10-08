using Autohand;
using Nox.XR.Runtime.Settings;
using UnityEngine;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Relie les bindings XR du déplacement/rotation/saut au <see cref="AutoHandPlayer"/>.
	///
	/// <para>
	/// Les valeurs sont relues à chaque frame auprès du runtime XR actif ; le saut est détecté sur
	/// le front montant de <c>jump</c> (le binding est un axe, pas un bouton).
	/// </para>
	///
	/// <para>
	/// Le mode de rotation (crans ou continue) ainsi que l'angle/vitesse associés proviennent de
	/// <see cref="MovementSettings"/> et sont ré-appliqués dès qu'ils changent (Réglages XR).
	/// </para>
	/// </summary>
	public class KeybindControllerLink : MonoBehaviour {
		public AutoHandPlayer player;

		/// <summary>Dernière valeur de <c>jump</c>, pour ne sauter qu'au front montant.</summary>
		private float _jump;

		private void OnEnable() {
			MovementSettings.TurnModeChanged.AddListener(OnTurnModeChanged);
			MovementSettings.SnapTurnAngleChanged.AddListener(OnTurnOptionChanged);
			MovementSettings.SmoothTurnSpeedChanged.AddListener(OnTurnOptionChanged);
			ApplyTurnSettings();
		}

		private void OnDisable() {
			MovementSettings.TurnModeChanged.RemoveListener(OnTurnModeChanged);
			MovementSettings.SnapTurnAngleChanged.RemoveListener(OnTurnOptionChanged);
			MovementSettings.SmoothTurnSpeedChanged.RemoveListener(OnTurnOptionChanged);
		}

		private void OnTurnModeChanged(XRTurnMode _)
			=> ApplyTurnSettings();

		private void OnTurnOptionChanged(float _)
			=> ApplyTurnSettings();

		/// <summary>Reporte les options de mouvement (rotation, angle de snap, vitesse) sur le joueur AutoHand.</summary>
		private void ApplyTurnSettings() {
			if (player == null)
				return;

			player.rotationType    = MovementSettings.TurnMode == XRTurnMode.Smooth ? RotationType.smooth : RotationType.snap;
			player.snapTurnAngle   = MovementSettings.SnapTurnAngle;
			player.smoothTurnSpeed = MovementSettings.SmoothTurnSpeed;
		}

		private void FixedUpdate() {
			player.Move(Keybindings.GetVector2Value("move"));
			player.Turn(Keybindings.GetVector2Value("turn").x);
		}

		private void Update() {
			player.Move(Keybindings.GetVector2Value("move"));
			player.Turn(Keybindings.GetVector2Value("turn").x);

			var jump = Keybindings.GetFloatValue("jump");
			if (jump > 0.1f && _jump <= 0.1f)
				player.Jump();

			_jump = jump;
		}
	}
}

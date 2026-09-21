using Autohand;
using UnityEngine;

namespace Nox.XR.Runtime.Connectors {
	/// <summary>
	/// Relie les bindings XR du déplacement/rotation/saut au <see cref="AutoHandPlayer"/>.
	///
	/// <para>
	/// Les valeurs sont relues à chaque frame auprès du runtime XR actif ; le saut est détecté sur
	/// le front montant de <c>jump</c> (le binding est un axe, pas un bouton).
	/// </para>
	/// </summary>
	public class KeybindControllerLink : MonoBehaviour {
		public AutoHandPlayer player;

		/// <summary>Dernière valeur de <c>jump</c>, pour ne sauter qu'au front montant.</summary>
		private float _jump;

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

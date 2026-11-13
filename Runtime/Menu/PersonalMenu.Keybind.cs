using Nox.XR.Bindings;
using Nox.XR.Runtime;

namespace Nox.XR.Menu {
	/// <summary>
	/// Toggle bindings of the menu (<c>menu.left</c> / <c>menu.right</c>).
	/// <para>
	/// Both bindings are axes, so only the rising edge toggles: reading the raw value would open and close
	/// the menu every frame for as long as the button is held.
	/// </para>
	/// </summary>
	public partial class PersonalMenu {
		/// <summary>Last values of the bindings, to detect the rising edge.</summary>
		private float _menuLeft;
		private float _menuRight;

		private static readonly float PressThreshold = 0.1f;

		/// <summary>
		/// Polls the bindings every frame: they are raw inputs, nothing announces a press. It runs even while
		/// the menu does not exist yet, so a press is what asks for it instead of waiting on the UI mod.
		/// </summary>
		private void Update()
			=> UpdateKeybindings();

		private void UpdateKeybindings() {
			var left = Keybindings.GetFloatValue(XRBinding.MenuLeft);
			if (left > PressThreshold && _menuLeft <= PressThreshold)
				Toggle();
			_menuLeft = left;

			var right = Keybindings.GetFloatValue(XRBinding.MenuRight);
			if (right > PressThreshold && _menuRight <= PressThreshold)
				Toggle();
			_menuRight = right;
		}
	}
}

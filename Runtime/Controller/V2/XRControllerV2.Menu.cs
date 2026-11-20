using Nox.Controllers;
using Nox.UI;
using Nox.XR.Menu;
using UnityEngine;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Menu of the rig: the panel the player opens to reach the widgets, and the controller contract that lets
	/// a caller holding only an <see cref="IController"/> toggle it.
	/// </summary>
	public partial class XRControllerV2 : IControllerMenu {
		[Header("Menu")]
		[SerializeField, Tooltip("Component building and showing the main menu. Found on the player when empty.")]
		private PersonalMenu menu;

		/// <summary>Menu connector of this rig, or <c>null</c> when it could not be created.</summary>
		public PersonalMenu Menu
			=> menu;

		/// <summary>
		/// Menu of this rig through the controller contract: what a caller holding only an
		/// <see cref="IController"/> uses (explicit, because the public <see cref="Menu"/> exposes the
		/// component that also places the panel).
		/// </summary>
		IMenu IControllerMenu.Menu
			=> menu
			? menu.Menu
			: null;

		bool IControllerMenu.IsOpen {
			get => menu && menu.IsOpen;
			set {
				if (!menu)
					return;

				if (value)
					menu.Open();
				else
					menu.Close();
			}
		}

		/// <summary>
		/// Binds the panel to this rig: it travels with the player and is placed in front of its view.
		/// Called once the player is known (see <c>Awake</c>).
		/// </summary>
		private void SetupMenu() {
			if (!menu)
				return;

			menu.Origin = player.transform;
			menu.View   = player.headCamera;
		}
	}
}

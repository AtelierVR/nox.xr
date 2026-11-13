using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.Interactions;
using Nox.UI;
using Nox.XR.Runtime;
using UnityEngine;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Menu {
	/// <summary>
	/// Main menu of the player: it hosts the menu the UI mod builds, shows and hides it, places it in front
	/// of the view.
	/// <para>
	/// Two roles, two objects: <see cref="Container"/> is the container the UI mod builds the menu into
	/// (<see cref="IMenuProvider"/>), and the transform of this component is the panel the player sees.
	/// </para>
	/// <para>
	/// It builds no UI of its own - the content comes from <see cref="IUiAPI.Make"/> and this connector
	/// never falls back on anything else.
	/// </para>
	/// <para>
	/// It knows nothing of the player: the space it travels with and the view it opens in front of come
	/// from <see cref="Origin"/> and <see cref="View"/>, and the state it reaches is announced through
	/// <see cref="Opened"/> and <see cref="Closed"/>, which the owner (the rig) subscribes to.
	/// </para>
	/// <para>
	/// Split in partials, all in <c>Menu</c>: this file (lifecycle and creation), <c>PersonalMenu.Menu</c>
	/// (hosting and placement) and <c>PersonalMenu.Keybind</c> (toggle bindings).
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Menu/Personal Menu")]
	[DefaultExecutionOrder(FollowOrder)]
	public partial class PersonalMenu : MonoBehaviour {
		/// <summary>
		/// Execution order of the component: after the tracked parts and the grabbers, so a carried panel follows
		/// the hand of the current frame and never lags it.
		/// </summary>
		public const int FollowOrder = 10010;

		/// <summary>Raised when the menu becomes visible.</summary>
		public UnityEvent<PersonalMenu> Opened = new();

		/// <summary>Raised when the menu is hidden.</summary>
		public UnityEvent<PersonalMenu> Closed = new();

		[Header("Placement")]
		[SerializeField, Tooltip("Distance in front of the view where the menu opens, in metres.")]
		private float distance = 0.5f;

		/// <summary>Menu built by the UI mod into <see cref="Container"/>, or <c>null</c> while it does not exist.</summary>
		private IMenu _menu;

		private bool _generating;

		/// <summary>Cancels the creation when the menu goes away (the rig is disposed).</summary>
		private CancellationTokenSource _context;

		/// <summary>A toggle arrived before the UI mod built the menu: it is served as soon as it is.</summary>
		private bool _openRequested;

		private void Start() {
			_context = new CancellationTokenSource();
			SetActive(false);
			Generate(_context.Token).Forget();
		}

		private void OnDestroy() {
			// Cancel first: a creation in flight must not touch a rig that is being destroyed.
			_context?.Cancel();
			_context?.Dispose();
			_context = null;

			_menu?.Dispose();
			_menu = null;

			// The container is a plain child: it goes away with the rig, nothing to release here.
			Container = null;
		}

		/// <summary>
		/// Asks the UI mod for the menu and builds it into <see cref="Container"/>. Safe to call again: the
		/// menu is created once, and the call is skipped while a creation is already running.
		/// </summary>
		/// <param name="cancellationToken">Cancels the creation: nothing is kept when it fires.</param>
		/// <returns><c>false</c> when the UI mod is not available yet, the container is missing, or the call was cancelled.</returns>
		public async UniTask<bool> Generate(CancellationToken cancellationToken = default) {
			if (_menu != null || _generating)
				return _menu != null;

			var ui = Client.UiAPI;
			if (ui == null)
				return false;

			_generating = true;
			try {
				_menu = await ui.Make(this, cancellationToken);
				if (_menu == null || cancellationToken.IsCancellationRequested) {
					_menu = null;
					return false;
				}

				_menu.Active = false;

				if (_openRequested)
					Open();

				return true;
			} catch (System.OperationCanceledException) {
				_menu = null;
				return false;
			} catch (System.Exception e) {
				Logger.LogException(e, tag: nameof(PersonalMenu));
				_menu = null;
				return false;
			} finally {
				_generating = false;
			}
		}

		/// <summary>
		/// Anchors the panel where the settings ask for (head, player or world) and then applies the pose of
		/// the carried panel: both are world poses written after everything that moves the rig has run.
		/// </summary>
		private void LateUpdate() {
			UpdateAnchor();
		}
	}
}

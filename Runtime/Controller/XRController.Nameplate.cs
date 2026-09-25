using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.CCK.Nameplate;
using Nox.Nameplate;
using UnityEngine;
using Transform = UnityEngine.Transform;

namespace Nox.XR.Runtime {
	/// <summary>
	/// Nameplate binding for the XR controller: the controller owns a dedicated anchor above
	/// the player's head and asks <c>nox.nameplate</c> for an <see cref="INameplate"/>.
	/// <para>
	/// That plate is only the controller's <b>handle</b> on the nameplate system: it displays nothing
	/// (a player never sees their own name and no session can influence it) and carries the
	/// client-wide visibility driven by the menu provider through <c>Keys.Visible</c>.
	/// </para>
	/// </summary>
	public partial class XRController {
		/// <summary>Height of the plate anchor above the head camera.</summary>
		private const float NameplateAnchorHeight = 0.35f;

		/// <summary>Dedicated transform handed to the nameplate mod (above the local head).</summary>
		public Transform NameplateAnchor { get; private set; }

		/// <summary>The plate owned by this controller (local and private to it).</summary>
		public INameplate Nameplate
			=> _nameplate;

		private INameplate _nameplate;
		private bool       _creatingNameplate;

		private void Update() {
			// The nameplate mod may be loaded after this proxy was created.
			if (!_nameplate.IsAlive())
				SetupNameplate();
		}

		private void SetupNameplate() {
			if (_nameplate.IsAlive() || _creatingNameplate)
				return;

			var api = Client.NameplateAPI;
			if (api == null)
				return;

			if (NameplateAnchor == null) {
				if (player == null || player.headCamera == null)
					return;

				var go = new GameObject("Nameplate Anchor");
				go.transform.SetParent(player.headCamera.transform, false);
				go.transform.localPosition = new Vector3(0f, NameplateAnchorHeight, 0f);
				NameplateAnchor = go.transform;
			}

			CreateNameplateAsync(api, NameplateAnchor).Forget();
		}

		private async UniTaskVoid CreateNameplateAsync(INameplateAPI api, Transform anchor) {
			_creatingNameplate = true;
			try {
				var plate = await api.Instantiate(anchor);
				if (!plate.IsAlive())
					return;

				// The proxy may have been destroyed while instantiating.
				if (!this || !gameObject) {
					plate.Dispose();
					return;
				}

				_nameplate = plate;

				// The plate displays nothing on its own: it is only this controller's handle on the
				// nameplate system, carrying the client-wide visibility driven by the menu provider.
				// Its badges carry the local platform/engine (hidden while no user is bound).
				plate.SetClientBadges();
			} finally {
				_creatingNameplate = false;
			}
		}

		private void DisposeNameplate() {
			if (_nameplate.IsAlive())
				_nameplate.Dispose();
			_nameplate = null;

			if (NameplateAnchor != null) {
				NameplateAnchor.gameObject.Destroy();
				NameplateAnchor = null;
			}
		}
	}
}

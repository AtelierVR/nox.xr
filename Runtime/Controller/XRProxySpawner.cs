using System.Threading;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.Controllers;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime {
	/// <summary>
	/// Creates and removes the XR proxy: the v2 rig (the <c>Nox.XR.Player</c> controller) by default, the
	/// AutoHand proxy as a fallback.
	/// <para>
	/// The asset API is only reachable from this assembly, and this assembly cannot reference the v2
	/// assembly (which already depends on this one), so the v2 rig is created from its prefab and used
	/// through the <see cref="IController"/> contract only.
	/// </para>
	/// </summary>
	public static class XRProxySpawner {
		/// <summary>AutoHand proxy prefab (v1).</summary>
		public const string V1Prefab = "xr_proxy.prefab";

		/// <summary>New player proxy prefab (v2).</summary>
		public const string V2Prefab = "controller.prefab";

		/// <summary>Controller id of the v1 proxy.</summary>
		public const string V1Id = "xr";

		/// <summary>Controller id of the v2 proxy.</summary>
		public const string V2Id = "xr2";

		/// <summary>Whether the current controller is an XR proxy (v1 or v2).</summary>
		public static bool IsProxyCurrent() {
			var current = CurrentController();
			if (current == null)
				return false;

			var id = current.GetId();
			return id == V1Id || id == V2Id;
		}

		/// <summary>
		/// Creates the proxy matching the headset presence: the v2 rig when its prefab carries an
		/// <see cref="IController"/>, the AutoHand proxy otherwise.
		/// </summary>
		/// <param name="cancellationToken">Owns the creation: cancelled, no proxy is left half-built.</param>
		public static async UniTask<bool> MakeProxy(CancellationToken cancellationToken = default) {
			if (Client.Instance == null) {
				Logger.LogError("The XR client is not running: cannot create the XR proxy.", tag: nameof(XRProxySpawner));
				return false;
			}

			var instance = Client.Instance.InstantiateAsset(V2Prefab);
			if (instance) {
				var proxy = instance.GetComponent<IController>();
				if (proxy == null) {
					Logger.LogWarning($"'{V2Prefab}' has no {nameof(IController)}: falling back to the v1 proxy.", tag: nameof(XRProxySpawner));
					instance.Destroy();
				} else if (await Client.ControllerAPI.SetCurrent(proxy)) {
					return true;
				} else {
					Logger.LogWarning($"Failed to set the v2 proxy ({proxy.GetId()}) as current.", tag: nameof(XRProxySpawner));
					instance.Destroy();
					return false;
				}
			}

			return await XRController.Make(cancellationToken);
		}

		/// <summary>Removes the current XR proxy, if it is one.</summary>
		public static async UniTask<bool> RemoveProxy() {
			if (!IsProxyCurrent())
				return false;

			await Client.ControllerAPI.SetCurrent(null);
			return true;
		}

		/// <summary>Instantiates a proxy prefab by its asset name.</summary>
		/// <param name="prefabName">Asset name, see <see cref="V1Prefab"/> and <see cref="V2Prefab"/>.</param>
		/// <param name="parent">Optional parent.</param>
		/// <returns>The instance, or <c>null</c> when the asset is missing.</returns>
		public static GameObject Instantiate(string prefabName, Transform parent = null) {
			var client = Client.Instance;
			if (client == null) {
				Logger.LogError($"The XR client is not running: cannot instantiate '{prefabName}'.", tag: nameof(XRProxySpawner));
				return null;
			}

			return client.InstantiateAsset(prefabName, parent);
		}

		/// <summary>Instantiates the v2 proxy prefab (the <c>Nox.XR.Player</c> rig).</summary>
		public static GameObject InstantiateV2(Transform parent = null)
			=> Instantiate(V2Prefab, parent);

		/// <summary>Instantiates the v1 proxy prefab (the AutoHand rig).</summary>
		public static GameObject InstantiateV1(Transform parent = null)
			=> Instantiate(V1Prefab, parent);

		private static IController CurrentController()
			=> Client.Instance == null ? null : Client.ControllerAPI?.Current;
	}
}

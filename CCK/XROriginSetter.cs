using Unity.XR.CoreUtils;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.CCK.XR {
	/// <summary>
	/// Composant helper pour définir manuellement le XR Origin
	/// À placer sur le GameObject XR Origin/XR Rig
	/// </summary>
	public class XROriginSetter : MonoBehaviour {
		public static XROrigin GlobalOrigin;
		public XROrigin Origin;

		/// <summary>
		/// Récupère le XROrigin attaché à ce GameObject ou assigné dans l'inspector
		/// </summary>
		/// <returns></returns>
		public XROrigin GetXROrigin() {
			Origin ??= GetComponent<XROrigin>();
			
			if (!Origin)
				Logger.LogError($"No XROrigin found on {gameObject.name} or assigned in inspector!", this, tag: nameof(XROriginSetter));
			
			return Origin;
		}

		private void OnEnable()
			=> SetAsXROrigin();

		private void OnDisable() {
			if (GlobalOrigin != GetXROrigin())
				return;
			
			Logger.LogDebug($"Clearing XR Origin reference from: {gameObject.name}", this, tag: nameof(XROriginSetter));
			GlobalOrigin.Origin = null;
		}

		/// <summary>
		/// Définit ce transform comme XR Origin
		/// </summary>
		[ContextMenu("Set As XR Origin")]
		public void SetAsXROrigin() {
			Logger.LogDebug($"Setting XR Origin to: {gameObject.name}", this, tag: nameof(XROriginSetter));
			GlobalOrigin = GetXROrigin();
		}
	}
}
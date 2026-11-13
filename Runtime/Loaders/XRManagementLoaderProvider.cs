using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.XR.Loaders;
using UnityEngine.XR.Management;

namespace Nox.XR.Runtime.Loaders {
	/// <summary>
	/// Loader de repli de nox.xr : il ne choisit rien et laisse XR Plug-in Management démarrer
	/// le premier loader configuré qui répond (voir <see cref="XRManagementLoader.StartAsync"/>).
	///
	/// <para>
	/// Priorité 0 : il n'est retenu que si aucun mod de loader (<c>nox.xr.openxr</c>,
	/// <c>nox.xr.openvr</c>) n'est installé, ce qui préserve le comportement historique.
	/// </para>
	///
	/// <para>
	/// <b>Refusé sous Windows</b> : une session Windows tourne exclusivement sous OpenXR
	/// (<c>nox.xr.openxr</c>), alors que ce repli démarre n'importe quel loader configuré — exactement
	/// ce que la règle veut empêcher. Il reste le dernier recours sur les autres plateformes.
	/// </para>
	/// </summary>
	public sealed class XRManagementLoaderProvider : XRLoaderProviderBase {
		/// <summary>Priorité volontairement basse : les loaders explicites passent devant.</summary>
		public const int DefaultPriority = 0;

		public override string Id
			=> "xr-management";

		public override int Priority
			=> DefaultPriority;

		public override bool IsValid {
			get {
				if (PlatformExtensions.CurrentPlatform == Platform.Windows)
					return false;
				var manager = XRGeneralSettings.Instance?.Manager;
				return manager?.activeLoaders?.Any(l => l != null) ?? false;
			}
		}

		public override UniTask<bool> Initialize()
			=> XRManagementLoader.StartAsync();

		public override UniTask Deinitialize()
			=> XRManagementLoader.Stop();

    }
}

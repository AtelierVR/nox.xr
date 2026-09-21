using System.Linq;
using Cysharp.Threading.Tasks;
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
	/// </summary>
	public sealed class XRManagementLoaderProvider : IXRLoaderProvider {
		/// <summary>Priorité volontairement basse : les loaders explicites passent devant.</summary>
		public const int DefaultPriority = 0;

		public string Id
			=> "xr-management";

		public int Priority
			=> DefaultPriority;

		public bool IsValid {
			get {
				var manager = XRGeneralSettings.Instance?.Manager;
				return manager?.activeLoaders?.Any(l => l != null) ?? false;
			}
		}

		public UniTask<bool> Initialize()
			=> XRManagementLoader.StartAsync();

		public UniTask Deinitialize()
			=> XRManagementLoader.Stop();

    }
}

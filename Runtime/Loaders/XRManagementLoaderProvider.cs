using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.XR.Bindings;
using Nox.XR.Loaders;
using Nox.XR.Trackers;
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
	public sealed class XRManagementLoaderProvider : IXRLoaderProvider {
		/// <summary>Priorité volontairement basse : les loaders explicites passent devant.</summary>
		public const int DefaultPriority = 0;

		public string Id
			=> "xr-management";

		public int Priority
			=> DefaultPriority;

		/// <summary>
		/// Aucun binding : ce repli démarre ce que XR Plug-in Management a configuré sans savoir
		/// quel runtime répond, donc sans connaître les contrôles disponibles. Installer
		/// <c>nox.xr.openxr</c> ou <c>nox.xr.openvr</c> pour avoir des bindings XR.
		/// </summary>
		public IBinding Binding
			=> null;

		/// <summary>
		/// Aucun tracker propre : ce repli laisse XR Plug-in Management démarrer un loader inconnu de
		/// nox.xr, donc ses trackers ne sont lisibles que comme devices XR communs (le repli générique
		/// du suivi du corps entier, <c>FullBodyTrackers</c>).
		/// </summary>
		public ITrackerProvider Trackers
			=> null;

		public bool IsValid {
			get {
				// Windows est réservé à OpenXR : ce repli démarre ce que XR Plug-in Management a
				// configuré, donc il ne doit jamais pouvoir être retenu là-bas.
				if (PlatformExtensions.CurrentPlatform == Platform.Windows)
					return false;

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

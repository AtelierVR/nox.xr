using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using UnityEngine.XR.Management;

namespace Nox.XR.Loaders {
	/// <summary>
	/// Base class for XR loader providers.
	/// <para>
	/// It centralizes the feature container and the editor-facing defaults, so each loader only
	/// declares its identity, its platform rules and its lifecycle. A loader that does not override
	/// a feature simply leaves it <c>null</c>.
	/// </para>
	/// </summary>
	public abstract class XRLoaderProviderBase : IXRLoaderEditorProvider {
		/// <summary>Features owned by this loader. Fill only the ones that are implemented.</summary>
		public XRLoaderFeatures Features { get; } = new();

		public abstract string Id { get; }

		public abstract int Priority { get; }

		public virtual bool IsValid
			=> IsSupported(PlatformExtensions.CurrentPlatform);

		public virtual bool IsSupported(Platform platform)
			=> true;

		public virtual XRLoader Loader
			=> null;

		public abstract UniTask<bool> Initialize();

		public abstract UniTask Deinitialize();
	}
}

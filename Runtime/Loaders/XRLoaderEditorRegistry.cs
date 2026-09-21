using System.Collections.Generic;
using System.Linq;
using Nox.CCK.Events;
using Nox.XR.Loaders;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Loaders {
	/// <summary>
	/// Registre des <see cref="IXRLoaderEditorProvider"/> disponibles.
	///
	/// <para>
	/// Les providers s'enregistrent à l'initialisation de <b>leur</b> mod. Comme nox.xr s'initialise
	/// souvent avant eux (contrainte <c>after: xr</c>), l'éditeur ne peut pas les découvrir une fois
	/// pour toutes : il s'abonne à <see cref="Changed"/> et rejoue sa configuration à chaque arrivée.
	/// </para>
	/// </summary>
	public static class XRLoaderEditorRegistry {
		private static readonly List<IXRLoaderEditorProvider> Registered = new();

		/// <summary>
		/// Déclenché après chaque enregistrement ou retrait de provider.
		/// </summary>
		public static readonly NoxEvent Changed = new();

		/// <summary>
		/// Providers enregistrés, dans leur ordre d'arrivée.
		/// </summary>
		public static IReadOnlyList<IXRLoaderEditorProvider> Providers
			=> Registered;

		/// <summary>
		/// Enregistre un provider. Sans effet s'il l'est déjà (même <see cref="IXRLoaderProvider.Id"/>).
		/// </summary>
		public static void Register(IXRLoaderEditorProvider provider) {
			if (provider == null)
				return;

			if (Registered.Any(p => p.Id == provider.Id)) {
				Logger.LogDebug($"XR loader editor provider '{provider.Id}' is already registered.");
				return;
			}

			Registered.Add(provider);
			Changed.Invoke();
		}

		/// <summary>
		/// Retire un provider (au démontage du mod qui l'a enregistré).
		/// </summary>
		public static void Unregister(IXRLoaderEditorProvider provider) {
			if (provider == null || !Registered.Remove(provider))
				return;

			Changed.Invoke();
		}
	}
}

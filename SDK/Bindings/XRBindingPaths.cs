using System.Collections.Generic;
using UnityEngine.InputSystem;

namespace Nox.XR.Bindings {
	/// <summary>
	/// Petits utilitaires partagés par les providers de bindings pour composer et valider des
	/// chemins d'Input System.
	/// </summary>
	public static class XRBindingPaths {
		/// <summary>
		/// Layout utilisé quand un provider ne veut pas imposer le sien : tous les devices XR
		/// (« XRController », <c>ViveWand</c>, <c>OpenVROculusTouchController</c>, ...) en
		/// dérivent, donc un chemin <c>&lt;XRController&gt;</c> matche n'importe laquelle de ces
		/// manettes.
		/// </summary>
		public const string XRControllerLayout = "XRController";

		/// <summary>
		/// Usage d'Input System correspondant à la main (segment <c>{LeftHand}</c> d'un chemin).
		/// </summary>
		public static string GetUsage(XRBindingHand hand)
			=> hand == XRBindingHand.Right
				? "RightHand"
				: "LeftHand";

		/// <summary>
		/// Compose un chemin de binding : <c>&lt;Layout&gt;{LeftHand}/control</c>.
		/// </summary>
		/// <param name="hand">Main visée.</param>
		/// <param name="layout">Layout du device, ou <c>null</c> pour <see cref="XRControllerLayout"/>.</param>
		/// <param name="control">
		/// Nom du contrôle, éventuellement sous forme d'usage (<c>{Trigger}</c>).
		/// Un contrôle vide donne un chemin vers le device lui-même, jamais null.
		/// </param>
		public static string Build(XRBindingHand hand, string layout, string control) {
			var device = string.IsNullOrWhiteSpace(layout)     
                ? XRControllerLayout 
                : layout;
			var target = string.IsNullOrWhiteSpace(control)    
                ? string.Empty       
                : $"/{control}";
			return $"<{device}>{{{GetUsage(hand)}}}{target}";
		}

		/// <summary>
		/// Indique si <paramref name="path"/> correspond à un contrôle réellement présent dans
		/// un device actuellement connecté.
		/// </summary>
		public static bool Exists(string path)
			=> !string.IsNullOrWhiteSpace(path) 
                && InputSystem.FindControl(path) != null;

		/// <summary>
		/// Retourne le premier candidat qui existe vraiment, ou à défaut le premier candidat non
		/// vide.
		///
		/// <para>
		/// Le repli sur un candidat non résolu est volontaire : aucun contrôle n'existe tant que
		/// le device n'est pas connecté, et un chemin syntaxiquement correct se liera dès qu'il
		/// apparaîtra (Input System réévalue les bindings à l'ajout du device). C'est ce qui
		/// permet de résoudre tôt dans la session, sans attendre les manettes.
		/// </para>
		/// </summary>
		public static string Resolve(IEnumerable<string> candidates) {
			if (candidates == null)
				return null;

			string first = null;
			foreach (var candidate in candidates) {
				if (string.IsNullOrWhiteSpace(candidate))
					continue;

				if (Exists(candidate))
					return candidate;

				first ??= candidate;
			}

			return first;
		}
	}
}

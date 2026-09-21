namespace Nox.XR.Bindings {
	/// <summary>
	/// Main qui porte un <see cref="XRBinding"/>.
	/// </summary>
	public enum XRBindingHand {
		Left,
		Right,
	}

	/// <summary>
	/// Type de la valeur d'un binding logique. C'est le type que les consommateurs lisent
	/// (<c>GetFloatValue</c> / <c>GetVector2Value</c>) : il appartient donc au binding logique,
	/// pas au device qui l'alimente.
	/// </summary>
	public enum XRBindingValue {
		/// <summary>Valeur analogique à 1 axe (gâchette, grip, doigt, bouton...).</summary>
		Float,

		/// <summary>Valeur à 2 axes (stick ou trackpad).</summary>
		Vector2,
	}

	/// <summary>
	/// Binding logique XR : ce que nox.xr veut savoir (« menu gauche », « index droit », ...),
	/// indépendamment du runtime, du modèle de manette et du nom des contrôles exposés.
	///
	/// <para>
	/// nox.xr ne connaît <b>aucun</b> chemin d'Input System : chaque mod de loader
	/// (<c>nox.xr.openxr</c>, <c>nox.xr.openvr</c>, ...) enregistre lui-même les chemins qui
	/// correspondent aux devices qu'il pilote et les expose aux consommateurs via l'<c>IBinding</c>
	/// rendu par <c>IXRLoaderProvider.Binding</c>. Ajouter le support d'une manette = modifier les
	/// chemins dans son propre mod, sans toucher à nox.xr.
	/// </para>
	/// </summary>
	public enum XRBinding {
		MenuLeft,
		MenuRight,
		Jump,

		SelectLeft,
		SelectRight,
		ActivateLeft,
		ActivateRight,
		PressLeft,
		PressRight,

		FingerLeftThumb,
		FingerLeftIndex,
		FingerLeftMiddle,
		FingerLeftRing,
		FingerLeftPinky,

		FingerRightThumb,
		FingerRightIndex,
		FingerRightMiddle,
		FingerRightRing,
		FingerRightPinky,

		Move,
		Turn,
		ScrollLeft,
		ScrollRight,
	}

	/// <summary>
	/// Métadonnées stables d'un <see cref="XRBinding"/>.
	/// </summary>
	public static class XRBindingExtensions {
		/// <summary>
		/// Main ciblée par défaut.
		///
		/// <para>
		/// C'est la main utilisée par les providers livrés pour composer le chemin, mais elle
		/// n'est pas contraignante : un device qui n'a pas de manette à droite (ou un binding
		/// volontairement miroir, comme <see cref="XRBinding.Jump"/> sur une manette gauche)
		/// reste libre de viser l'autre main.
		/// </para>
		/// </summary>
		public static XRBindingHand GetHand(this XRBinding binding)
			=> binding switch {
				XRBinding.MenuLeft
					or XRBinding.SelectLeft
					or XRBinding.ActivateLeft
					or XRBinding.PressLeft
					or XRBinding.FingerLeftThumb
					or XRBinding.FingerLeftIndex
					or XRBinding.FingerLeftMiddle
					or XRBinding.FingerLeftRing
					or XRBinding.FingerLeftPinky
					or XRBinding.Jump
					or XRBinding.Move
					or XRBinding.ScrollLeft
					=> XRBindingHand.Left,
				_ => XRBindingHand.Right,
			};

		/// <summary>
		/// Identifiant du binding dans le système de key bindings (<c>Nox.KeyBindings</c>) :
		/// c'est la clé de configuration (<c>settings.key_bindings.&lt;catégorie&gt;.&lt;clé&gt;</c>)
		/// et la chaîne comparée par les connecteurs du prefab XR
		/// (ex. <c>FingerKeybindConnector.BindKey</c>).
		///
		/// <para>
		/// <b>Ne jamais la changer</b> : les overrides sauvegardés par les joueurs et les valeurs
		/// sérialisées dans les prefabs s'y réfèrent.
		/// </para>
		/// </summary>
		public static string GetKey(this XRBinding binding)
			=> binding switch {
				XRBinding.MenuLeft   => "menu.left",
				XRBinding.MenuRight  => "menu.right",
				XRBinding.Jump       => "jump",
				XRBinding.SelectLeft => "select.left",
				XRBinding.SelectRight => "select.right",
				XRBinding.ActivateLeft => "activate.left",
				XRBinding.ActivateRight => "activate.right",
				XRBinding.PressLeft  => "press.left",
				XRBinding.PressRight => "press.right",
				XRBinding.FingerLeftThumb  => "finger.left.thumb",
				XRBinding.FingerLeftIndex  => "finger.left.index",
				XRBinding.FingerLeftMiddle => "finger.left.middle",
				XRBinding.FingerLeftRing   => "finger.left.ring",
				XRBinding.FingerLeftPinky  => "finger.left.pinky",
				XRBinding.FingerRightThumb  => "finger.right.thumb",
				XRBinding.FingerRightIndex  => "finger.right.index",
				XRBinding.FingerRightMiddle => "finger.right.middle",
				XRBinding.FingerRightRing   => "finger.right.ring",
				XRBinding.FingerRightPinky  => "finger.right.pinky",
				XRBinding.Move        => "move",
				XRBinding.Turn        => "turn",
				XRBinding.ScrollLeft  => "scroll.left",
				XRBinding.ScrollRight => "scroll.right",
				_                     => binding.ToString().ToLowerInvariant(),
			};

		/// <summary>
		/// Catégorie du binding dans le système de key bindings (regroupement affiché dans les
		/// réglages et préfixe du chemin de configuration).
		/// </summary>
		public static string GetCategory(this XRBinding binding)
			=> binding switch {
				XRBinding.Jump or XRBinding.Move or XRBinding.Turn => "nox.movement",
				XRBinding.MenuLeft
					or XRBinding.MenuRight
					or XRBinding.PressLeft
					or XRBinding.PressRight
					or XRBinding.ScrollLeft
					or XRBinding.ScrollRight
					=> "nox.ui",
				_ => "nox.hand",
			};

		/// <summary>
		/// Type de la valeur lue par les consommateurs : les axes de déplacement, de rotation et de
		/// défilement sont à 2 dimensions, tout le reste est analogique 1 axe.
		/// </summary>
		public static XRBindingValue GetValue(this XRBinding binding)
			=> binding switch {
				XRBinding.Move or XRBinding.Turn or XRBinding.ScrollLeft or XRBinding.ScrollRight
					=> XRBindingValue.Vector2,
				_ => XRBindingValue.Float,
			};
	}
}

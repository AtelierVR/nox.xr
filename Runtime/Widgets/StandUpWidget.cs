using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.UI;
using Nox.UI.Widgets;
using UnityEngine;
using UnityEngine.UI;

namespace Nox.XR.Runtime.Widgets {
	/// <summary>
	/// Widget « Stand up » : recentre la vue du rig et la remet à la hauteur recommandée
	/// (voir <see cref="IXRController.ReCenterAndReHeight"/>).
	/// <para>
	/// Il n'a de sens qu'en XR : <see cref="TryMake"/> renvoie faux si le contrôleur courant
	/// n'est pas un <see cref="IXRController"/>, et le widget est ajouté/retiré à chaud quand
	/// le contrôleur courant change (voir <see cref="Client"/>).
	/// </para>
	/// </summary>
	public class StandUpWidget : MonoBehaviour, IWidget {
		public static string GetDefaultKey()
			=> "stand_up";

		/// <summary>
		/// Instances vivantes, pour savoir si le widget est déjà affiché.
		/// </summary>
		internal static readonly HashSet<StandUpWidget> All = new();

		/// <summary>
		/// Dernier conteneur de widgets fourni par une page, mémorisé pour pouvoir recréer le
		/// bouton hors d'une requête « widget_request » (quand on entre en XR page ouverte).
		/// </summary>
		private static IMenu _menu;
		private static RectTransform _parent;

		private GameObject _content;

		private void Awake()
			=> All.Add(this);

		private void OnDestroy()
			=> All.Remove(this);

		public string GetKey()
			=> GetDefaultKey();

		public Vector2Int GetSize()
			=> Vector2Int.one;

		/// <summary>
		/// Avant les widgets de navigation : c'est une action de secours.
		/// </summary>
		public int GetPriority()
			=> 101;

		/// <summary>
		/// Vrai si le contrôleur courant est le proxy XR.
		/// </summary>
		public static bool IsAvailable()
			=> Client.ControllerAPI?.Current is IXRController;

		/// <summary>
		/// Recentre la vue et la remet à la hauteur recommandée pour l'avatar courant.
		/// </summary>
		public static void StandUp() {
			if (Client.ControllerAPI?.Current is not IXRController controller)
				return;
			controller.ReCenterAndReHeight();
		}

		private void OnClick()
			=> StandUp();

		public static bool TryMake(IMenu menu, RectTransform parent, out (GameObject, IWidget) values) {
			// Mémorisé avant le test : la page peut demander ses widgets alors qu'on n'est pas
			// encore en XR, et on veut pouvoir ajouter le bouton dès qu'on y entre.
			_menu   = menu;
			_parent = parent;

			if (!IsAvailable()) {
				values = (null, null);
				return false;
			}

			var prefab    = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("ui:prefabs/grid_item.prefab");
			var instance  = prefab.Instantiate(parent);
			var component = instance.AddComponent<StandUpWidget>();

			var button = Reference.GetComponent<Button>("button", instance);
			button.onClick.AddListener(component.OnClick);
			instance.name = $"[{component.GetKey()}_{instance.GetEntityId().GetHashCode()}]";
			values        = (instance, component);

			prefab             = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("ui:prefabs/widget.prefab");
			component._content = prefab.Instantiate(Reference.GetComponent<RectTransform>("content", instance));

			component.UpdateIcon().Forget();

			return true;
		}

		/// <summary>
		/// Ajoute le bouton si le contrôleur courant est une XR et que la page est ouverte.
		/// </summary>
		internal static void Show() {
			if (All.Count > 0 || !_parent || _menu == null || !IsAvailable())
				return;
			if (!TryMake(_menu, _parent, out var values) || values.Item2 == null)
				return;
			Client.CoreAPI.EventAPI.Emit("widget_added", values.Item2);
		}

		/// <summary>
		/// Retire le bouton (le contrôleur courant n'est plus le proxy XR).
		/// </summary>
		internal static void Hide() {
			if (All.Count == 0)
				return;
			Client.CoreAPI.EventAPI.Emit("widget_removed", GetDefaultKey());
		}

		private async UniTask UpdateIcon() {
			var icon      = await Client.CoreAPI.AssetAPI.GetAssetAsync<Sprite>("ui:icons/person.png");
			var labelIcon = Reference.GetComponent<Image>("icon", _content);
			labelIcon.sprite = icon;
		}
	}
}

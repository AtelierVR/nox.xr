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
	/// "Stand up" widget: recentres the rig's view and brings it back to the recommended height (see
	/// <see cref="IXRController.ReCenterAndReHeight"/>). Added or removed live when the current controller
	/// changes.
	/// </summary>
	public class StandUpWidget : MonoBehaviour, IWidget {
		public static string GetDefaultKey()
			=> "stand_up";

		/// <summary>Live instances.</summary>
		internal static readonly HashSet<StandUpWidget> All = new();

		/// <summary>Last widget container a page provided (see <see cref="Show"/>).</summary>
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

		/// <summary>Before the navigation widgets: a fallback action.</summary>
		public int GetPriority()
			=> 101;

		/// <summary>True when the current controller is the XR proxy.</summary>
		public static bool IsAvailable()
			=> Client.ControllerAPI?.Current is IXRController;

		/// <summary>Recentres the view and brings it to the height recommended for the current avatar.</summary>
		public static void StandUp() {
			if (Client.ControllerAPI?.Current is not IXRController controller)
				return;
			controller.ReCenterAndReHeight();
		}

		private void OnClick()
			=> StandUp();

		/// <summary>Builds the grid item, or returns false when the button is not available.</summary>
		public static bool TryMake(IMenu menu, RectTransform parent, out (GameObject, IWidget) values) {
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
			instance.name = $"[{component.GetKey()}_{instance.GetId()}]";
			values        = (instance, component);

			prefab             = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("ui:prefabs/widget.prefab");
			component._content = prefab.Instantiate(Reference.GetComponent<RectTransform>("content", instance));

			component.UpdateIcon().Forget();

			return true;
		}

		/// <summary>
		/// Adds the button to the current page unless it is already there. The menu keeps several copies of each
		/// page, so the test is per page and not on the global instance count.
		/// </summary>
		internal static void Show() {
			if (!_parent || _menu == null || !IsAvailable())
				return;

			if (_parent.GetComponentInChildren<StandUpWidget>(true))
				return;

			if (!TryMake(_menu, _parent, out var values) || values.Item2 == null)
				return;
			Client.CoreAPI.EventAPI.Emit("widget_added", values.Item2);
		}

		/// <summary>
		/// Removes the button from every page: subscribed pages react to the event, the remaining copies
		/// (closed pages) are destroyed here.
		/// </summary>
		internal static void Hide() {
			if (All.Count == 0)
				return;

			Client.CoreAPI.EventAPI.Emit("widget_removed", GetDefaultKey());
			foreach (var widget in All)
				if (widget)
					widget.gameObject.Destroy();
		}

		private async UniTask UpdateIcon() {
			var icon      = await Client.CoreAPI.AssetAPI.GetAssetAsync<Sprite>("ui:icons/person.png");
			var labelIcon = Reference.GetComponent<Image>("icon", _content);
			labelIcon.sprite = icon;
		}
	}
}

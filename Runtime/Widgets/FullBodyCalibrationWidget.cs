using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.UI;
using Nox.UI.Widgets;
using Nox.XR.Runtime.FullBody;
using Nox.XR.Runtime.Settings;
using UnityEngine;
using UnityEngine.UI;

namespace Nox.XR.Runtime.Widgets {
	/// <summary>
	/// Full-body calibration widget: starts a calibration, or confirms the one running — the same action as
	/// <see cref="CalibrateFullBodySetting"/>, without opening the menu.
	/// <para>
	/// Only available when a calibration makes sense: the XR proxy is current and at least one tracker is
	/// usable (<see cref="IsAvailable"/>).
	/// </para>
	/// </summary>
	public class FullBodyCalibrationWidget : MonoBehaviour, IWidget {
		public static string GetDefaultKey()
			=> "full_body_calibration";

		/// <summary>Live instances.</summary>
		internal static readonly HashSet<FullBodyCalibrationWidget> All = new();

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

		/// <summary>Right after <see cref="StandUpWidget"/> (101): a tracking action, not a fallback.</summary>
		public int GetPriority()
			=> 102;

		/// <summary>
		/// True when the XR proxy is the current controller and <see cref="FullBodyCalibration.TrackerCount"/>
		/// reports at least one tracker.
		/// </summary>
		public static bool IsAvailable()
			=> Client.ControllerAPI?.Current is IXRController
			   && FullBodyCalibration.TrackerCount > 0;

		/// <summary>Starts the calibration, or confirms it when one is running.</summary>
		private void OnClick() {
			var calibration = FullBodyCalibration.Instance;
			if (calibration == null)
				return;

			if (calibration.IsCalibrating)
				calibration.ConfirmCalibration();
			else
				calibration.StartCalibration();
		}

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
			var component = instance.AddComponent<FullBodyCalibrationWidget>();

			var button = Reference.GetComponent<Button>("button", instance);
			button.onClick.AddListener(component.OnClick);
			instance.name = $"[{component.GetKey()}_{instance.GetId()}]";
			values        = (instance, component);

			prefab             = Client.CoreAPI.AssetAPI.GetAsset<GameObject>("ui:prefabs/widget.prefab");
			component._content = prefab.Instantiate(Reference.GetComponent<RectTransform>("content", instance));

			component.UpdateIcon().Forget();

			return true;
		}

		/// <summary>Adds or removes the button to match the current state.</summary>
		internal static void Refresh() {
			if (IsAvailable())
				Show();
			else
				Hide();
		}

		/// <summary>
		/// Adds the button to the current page unless it is already there. The menu keeps several copies of each
		/// page, so the test is per page and not on the global instance count.
		/// </summary>
		internal static void Show() {
			if (!_parent || _menu == null || !IsAvailable())
				return;

			if (_parent.GetComponentInChildren<FullBodyCalibrationWidget>(true))
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
			var icon         = await Client.CoreAPI.AssetAPI.GetAssetAsync<Sprite>("ui:icons/fbt.png");
			var labelIcon    = Reference.GetComponent<Image>("icon", _content);
			labelIcon.sprite = icon;
		}
	}
}

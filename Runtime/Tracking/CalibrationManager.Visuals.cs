using System.Collections.Generic;
using System.Linq;
using Autohand;
using Nox.Avatars.Parameters;
using Nox.Avatars.Rigging;
using Nox.Avatars.Scale;
using Nox.Avatars.StateMachines;
using Nox.CCK.Avatars.Playable;
using Nox.CCK.Avatars.Rigging;
using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.CCK.XR;
using Nox.Settings;
using Nox.XR.Calibration;
using CalibrationService = Nox.XR.Calibration.Calibration;
using Nox.XR.Runtime.Connectors;
using Nox.XR.Runtime.Settings;
using Nox.XR.Trackers;
using UnityEngine;
using UnityEngine.XR;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Runtime.Tracking {
	
	public partial class CalibrationManager : MonoBehaviour {

		private void CreateVisuals() {
			ClearVisuals();

			_visualsRoot = new GameObject("FBT Calibration Visuals").transform;
			_visualsRoot.SetParent(player ? player.transform : transform, false);
		}

		private void UpdateTrackerVisuals() {
			while (_trackerVisuals.Count < _trackers.Count)
				_trackerVisuals.Add(CreateVisual(TrackerVisualAddress));

			for (var i = 0; i < _trackerVisuals.Count; i++) {
				var visual = _trackerVisuals[i];
				if (!visual)
					continue;

				var active = i < _trackers.Count;
				visual.SetActive(active);
				if (active)
					visual.transform.SetPositionAndRotation(_trackers[i].Position, _trackers[i].Rotation);
			}
		}

		private void UpdateBoneVisuals(IRigging rig) {
			var bones = GetAssignableBones(rig);
			if (bones.Count == 0)
				return;

			foreach (var bone in bones) {
				var boneTransform = rig?.GetBone(bone);
				if (!boneTransform)
					continue;

				if (!_boneVisuals.TryGetValue(bone, out var marker) || !marker) {
					marker = CreateVisual(BoneVisualAddress);
					_boneVisuals[bone] = marker;
				}
				if (marker)
					marker.transform.SetPositionAndRotation(boneTransform.position, boneTransform.rotation);

				if (!_rangeVisuals.TryGetValue(bone, out var rangeVisual) || !rangeVisual) {
					rangeVisual = CreateVisual(RangeVisualAddress);
					_rangeVisuals[bone] = rangeVisual;
				}
				if (rangeVisual) {
					rangeVisual.transform.SetPositionAndRotation(boneTransform.position, boneTransform.rotation);
					SetWorldRadius(rangeVisual.transform, LinkedDistance(bone, boneTransform.position));
				}
			}
		}

		/// <summary>Distance between <paramref name="bone"/> and its tracker, 0 when there is none.</summary>
		private float LinkedDistance(HumanBodyBones bone, Vector3 bonePosition)
			=> _matches.TryGetValue(bone, out var tracker)
				? Vector3.Distance(bonePosition, tracker.Position)
				: 0f;

		/// <summary>
		/// Applies a world radius to a visual whose sphere is one unit across, compensating the parent scale:
		/// the visuals are children of the player, which carries the avatar scale.
		/// </summary>
		private static void SetWorldRadius(Transform visual, float radius) {
			const float diameter = 2f;

			var parent = visual.parent;
			var scale  = parent ? parent.lossyScale : Vector3.one;

			visual.localScale = new Vector3(
				radius * diameter / Mathf.Max(0.0001f, Mathf.Abs(scale.x)),
				radius * diameter / Mathf.Max(0.0001f, Mathf.Abs(scale.y)),
				radius * diameter / Mathf.Max(0.0001f, Mathf.Abs(scale.z))
			);
		}

		private GameObject CreateVisual(string address) {
			var prefab = Client.CoreAPI?.AssetAPI?.GetAsset<GameObject>(address);
			if (!prefab) {
				Logger.LogWarning($"Calibration visual prefab not found: {address}", this, tag: nameof(CalibrationManager));
				return null;
			}

			return prefab.Instantiate(_visualsRoot);
		}

		private void ClearVisuals() {
			foreach (var visual in _trackerVisuals)
				if (visual)
					visual.Destroy();
			_trackerVisuals.Clear();

			foreach (var visual in _boneVisuals.Values)
				if (visual)
					visual.Destroy();
			_boneVisuals.Clear();

			foreach (var visual in _rangeVisuals.Values)
				if (visual)
					visual.Destroy();
			_rangeVisuals.Clear();

			if (_visualsRoot) {
				_visualsRoot.gameObject.Destroy();
				_visualsRoot = null;
			}
		}

	}
}

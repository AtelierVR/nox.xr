using Nox.CCK.Entities;
using Nox.CCK.Utils;
using Nox.XR.Runtime.Tracking;
using Nox.XR.Trackers;
using UnityEngine;

namespace Nox.XR.Player {
	/// <summary>
	/// Feeds a <see cref="Part"/> from the loader-owned tracker providers.
	/// <para>
	/// The part only says which <see cref="EntityRig"/> it occupies; the tracker layer resolves the
	/// matching pose, velocity and angular velocity from the active loader/bindings/calibration.
	/// When the source disappears the part is disabled, then re-enabled when it comes back.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/XR Follow")]
	[DefaultExecutionOrder(1000)]
	public class XRFollow : MonoBehaviour {
		[SerializeField, Tooltip("Part driven by this follower. Found on this GameObject when empty.")]
		private Part part;

		[SerializeField, Tooltip("Follow the node. Disable to freeze this part without disabling it.")]
		private bool follow = true;

		[SerializeField, Tooltip("Free tracker serial when the part has no EntityRig. Empty uses FreeTrackerIndex.")]
		private string freeTrackerId;

		[SerializeField, Tooltip("Index among free (unclaimed) trackers when the part has no EntityRig.")]
		private int freeTrackerIndex;

		[SerializeField, Tooltip("Forward the loader velocity and angular velocity to the part. Hands compute their own velocity from their follow target, so this is off by default.")]
		private bool applyVelocity;

		[SerializeField, Tooltip("Disable the part while the tracked source is missing (untracked/disconnected).")]
		private bool disableWhenMissing = true;

		[SerializeField, Tooltip("Apply the tracked pose in Update as well, so the avatar rig (solved in LateUpdate) reads the pose of the current frame. LateUpdate re-applies it after the player moved its tracking space, so the view is still written last.")]
		private bool applyInUpdate = true;

		[SerializeField, Tooltip("Apply the pose once more right before the view camera renders (head only), the last chance to write the view before it is drawn.")]
		private bool applyBeforeRender = true;

		private bool _disabledByMissing;

		public Part Part {
			get => part;
			set => part = value;
		}

		public bool Follow {
			get => follow;
			set => follow = value;
		}

		public string FreeTrackerId {
			get => freeTrackerId;
			set => freeTrackerId = value;
		}

		public int FreeTrackerIndex {
			get => freeTrackerIndex;
			set => freeTrackerIndex = value;
		}

		public bool ApplyVelocity {
			get => applyVelocity;
			set => applyVelocity = value;
		}

		public bool DisableWhenMissing {
			get => disableWhenMissing;
			set => disableWhenMissing = value;
		}

		/// <summary>Whether the pose is applied in <c>Update</c> as well as in <c>LateUpdate</c>.</summary>
		public bool ApplyInUpdate {
			get => applyInUpdate;
			set => applyInUpdate = value;
		}

		/// <summary>Whether the pose is applied again right before the view camera renders.</summary>
		public bool ApplyBeforeRender {
			get => applyBeforeRender;
			set {
				if (applyBeforeRender == value)
					return;

				applyBeforeRender = value;
				SubscribeRender(value);
			}
		}

		private void Reset() => part = GetComponent<Part>();

		private void OnValidate() {
			if (!part)
				part = GetComponent<Part>();
		}

		private void Awake() {
			if (!part)
				part = GetComponent<Part>();
		}

		private void OnEnable()
			=> SubscribeRender(true);

		private void OnDisable()
			=> SubscribeRender(false);

		/// <summary>
		/// Subscribes the last chance to write the view: the pose applied right before the head camera
		/// renders, i.e. after every <c>LateUpdate</c> of the frame (see <c>nox.desktop</c>, which places its
		/// camera the same way). Only the head part carries the view camera, so the other parts skip it.
		/// </summary>
		private void SubscribeRender(bool subscribe) {
			if (subscribe) {
				if (!applyBeforeRender || part is not Head)
					return;

				RenderOrder.Subscribe(OnBeginCameraRendering, RenderPhase.Input);
				return;
			}

			RenderOrder.Unsubscribe(OnBeginCameraRendering);
		}

		private void OnBeginCameraRendering(Camera camera) {
			if (!applyBeforeRender || !follow || !part || !part.PartEnabled)
				return;

			if (part is not Head head || camera != head.Camera)
				return;

			ApplyNow();
		}

		/// <summary>
		/// Resolves the tracked pose and writes it now. Used by the late passes (end of <c>LateUpdate</c> and
		/// before the camera renders) so the view is written after everything else in the frame.
		/// </summary>
		/// <returns><c>false</c> when the part is frozen or the tracked source has no pose.</returns>
		public bool ApplyNow() {
			if (!isActiveAndEnabled || !follow || !part || !part.PartEnabled)
				return false;

			if (!TryGetPose(out var pose))
				return false;

			ApplyPose(pose);
			return true;
		}

		/// <summary>
		/// Applies the tracked pose <b>before</b> the late update, so everything that reads the part in
		/// LateUpdate (the avatar rig, which solves there) sees the pose of the current frame instead of
		/// the previous one.
		/// <para>
		/// The pose is applied again in <see cref="LateUpdate"/> (execution order 1000), after the player
		/// moved its tracking space (order 1) and after the avatar rig solved: the view - the camera
		/// carried by the head part - is the last transform written in the frame, so it never inherits
		/// the move of its parent and the gaze keeps no extra frame of latency.
		/// </para>
		/// </summary>
		private void Update() {
			if (!applyInUpdate)
				return;

			ApplyNow();
		}

		private void LateUpdate() {
			if (!follow || !part)
				return;

			// A part disabled by hand stays untouched; only a part disabled because its source went
			// away is re-enabled automatically when the source comes back.
			if (!part.PartEnabled && !_disabledByMissing)
				return;

			if (!TryGetPose(out var pose)) {
				if (disableWhenMissing && part.PartEnabled) {
					part.SetEnabled(false);
					_disabledByMissing = true;
				}

				return;
			}

			if (_disabledByMissing) {
				part.SetEnabled(true);
				_disabledByMissing = false;
			}

			ApplyPose(pose);
		}

		/// <summary>
		/// Writes a resolved pose on the part (the part decides what it drives).
		/// <para>
		/// The pose is already in world space (<see cref="TrackerResolver.TryGetPose(ushort, out TrackerPose)"/> applies
		/// the tracking space, the avatar fit and the calibration): transforming it again here would move a calibrated
		/// bone by the container rotation and stretch its offset with the fit.
		/// </para>
		/// </summary>
		private void ApplyPose(TrackerPose pose) {
			part.SetPose(pose.Position, pose.Rotation);

			if (!applyVelocity)
				return;

			part.SetVelocity(pose.Velocity);
			part.SetAngularVelocity(pose.AngularVelocity);
		}

		private bool TryGetPose(out TrackerPose pose) {
			if (part.Rig != EntityRig.None)
				return TrackerResolver.TryGetPose(part.Rig, out pose);

			return string.IsNullOrEmpty(freeTrackerId)
				? TrackerResolver.TryGetFreeTracker(freeTrackerIndex, out pose)
				: TrackerResolver.TryGetFreeTracker(freeTrackerId, out pose);
		}
	}
}

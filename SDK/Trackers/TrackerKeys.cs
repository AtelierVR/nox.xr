namespace Nox.XR.Trackers {
	/// <summary>
	/// Keys accepted by <see cref="ITracker.Get{T}(string)"/>. Like <c>IBinding.Get</c>, a runtime
	/// returns <c>default</c> for a key it does not expose instead of throwing.
	/// </summary>
	public static class TrackerKeys {
		/// <summary>Battery level, 0..1 (<c>float</c>).</summary>
		public const string BatteryLevel = "battery.level";

		/// <summary>Whether the device is tracked right now (<c>bool</c>).</summary>
		public const string IsTracked = "tracking.is_tracked";

		/// <summary>Whether the user is wearing/interacting with the device (<c>bool</c>).</summary>
		public const string UserPresence = "tracking.user_presence";

		/// <summary>Runtime tracking state (<c>InputTrackingState</c>).</summary>
		public const string TrackingState = "tracking.state";

		/// <summary>Raw position in tracking space (<c>Vector3</c>).</summary>
		public const string Position = "pose.position";

		/// <summary>Raw rotation in tracking space (<c>Quaternion</c>).</summary>
		public const string Rotation = "pose.rotation";

		/// <summary>Linear velocity in tracking space (<c>Vector3</c>).</summary>
		public const string Velocity = "pose.velocity";

		/// <summary>Angular velocity in tracking space (<c>Vector3</c>).</summary>
		public const string AngularVelocity = "pose.angular_velocity";

		// Input values exposed by controllers and handheld devices. They use the same
		// CommonUsages features as Unity's InputDevice, read by the loader providers.

		/// <summary>Primary button pressed (<c>bool</c>).</summary>
		public const string PrimaryButton = "input.primary.button";

		/// <summary>Primary button touched (<c>bool</c>).</summary>
		public const string PrimaryTouch = "input.primary.touch";

		/// <summary>Secondary button pressed (<c>bool</c>).</summary>
		public const string SecondaryButton = "input.secondary.button";

		/// <summary>Secondary button touched (<c>bool</c>).</summary>
		public const string SecondaryTouch = "input.secondary.touch";

		/// <summary>Primary 2D axis, e.g. thumbstick (<c>Vector2</c>).</summary>
		public const string Primary2DAxis = "input.primary.2d_axis";

		/// <summary>Primary 2D axis clicked (<c>bool</c>).</summary>
		public const string Primary2DAxisClick = "input.primary.2d_axis.click";

		/// <summary>Primary 2D axis touched (<c>bool</c>).</summary>
		public const string Primary2DAxisTouch = "input.primary.2d_axis.touch";

		/// <summary>Secondary 2D axis (<c>Vector2</c>).</summary>
		public const string Secondary2DAxis = "input.secondary.2d_axis";

		/// <summary>Secondary 2D axis clicked (<c>bool</c>).</summary>
		public const string Secondary2DAxisClick = "input.secondary.2d_axis.click";

		/// <summary>Secondary 2D axis touched (<c>bool</c>).</summary>
		public const string Secondary2DAxisTouch = "input.secondary.2d_axis.touch";

		/// <summary>Trigger analog value (<c>float</c>).</summary>
		public const string Trigger = "input.trigger";

		/// <summary>Trigger pressed (<c>bool</c>).</summary>
		public const string TriggerButton = "input.trigger.button";

		/// <summary>Grip analog value (<c>float</c>).</summary>
		public const string Grip = "input.grip";

		/// <summary>Grip pressed (<c>bool</c>).</summary>
		public const string GripButton = "input.grip.button";

		/// <summary>Menu button pressed (<c>bool</c>).</summary>
		public const string MenuButton = "input.menu.button";

		/// <summary>Linear acceleration in tracking space (<c>Vector3</c>).</summary>
		public const string Acceleration = "pose.acceleration";

		// Hand data, exposed by devices declaring <see cref="TrackerFeatures.Skeleton"/>. The curls go
		// from 0 (fully extended) to 1 (fully curled), like the poses of the avatar's fingers
		// (see Nox.Avatars.Hand.FingerCurl): a consumer can blend them directly.

		/// <summary>Whether a hand is tracked right now (<c>bool</c>).</summary>
		public const string Hand = "hand.active";

		/// <summary>Thumb curl, 0..1 (<c>float</c>).</summary>
		public const string HandThumb = "hand.curl.thumb";

		/// <summary>Index curl, 0..1 (<c>float</c>).</summary>
		public const string HandIndex = "hand.curl.index";

		/// <summary>Middle curl, 0..1 (<c>float</c>).</summary>
		public const string HandMiddle = "hand.curl.middle";

		/// <summary>Ring curl, 0..1 (<c>float</c>).</summary>
		public const string HandRing = "hand.curl.ring";

		/// <summary>Little (pinky) curl, 0..1 (<c>float</c>).</summary>
		public const string HandLittle = "hand.curl.little";

		/// <summary>Thumb tip ↔ index tip pinch, 0 (apart) .. 1 (touching) (<c>float</c>).</summary>
		public const string HandPinch = "hand.pinch";

		/// <summary>Angular acceleration in tracking space (<c>Vector3</c>).</summary>
		public const string AngularAcceleration = "pose.angular_acceleration";

		/// <summary>Left eye position in tracking space (<c>Vector3</c>).</summary>
		public const string LeftEyePosition = "eye.left.position";

		/// <summary>Left eye rotation in tracking space (<c>Quaternion</c>).</summary>
		public const string LeftEyeRotation = "eye.left.rotation";

		/// <summary>Right eye position in tracking space (<c>Vector3</c>).</summary>
		public const string RightEyePosition = "eye.right.position";

		/// <summary>Right eye rotation in tracking space (<c>Quaternion</c>).</summary>
		public const string RightEyeRotation = "eye.right.rotation";

		/// <summary>Center eye position in tracking space (<c>Vector3</c>).</summary>
		public const string CenterEyePosition = "eye.center.position";

		/// <summary>Center eye rotation in tracking space (<c>Quaternion</c>).</summary>
		public const string CenterEyeRotation = "eye.center.rotation";
	}
}

using System.Collections.Generic;
using Nox.Avatars;
using Nox.Avatars.Rigging;
using Nox.CCK.Entities;
using Nox.XR.Player;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Rig side of the v2 XR controller: it binds the tracked parts of the player onto the rig of
	/// the loaded avatar.
	/// <para>
	/// The rig lives in a module of the avatar (<see cref="IRigProvider"/>), which the avatar announces
	/// through its descriptor: the provider is resolved on the register/unregister events, never polled.
	/// </para>
	/// <para>
	/// The binding itself is <b>event based</b> too: a part is bound to the rig when it is registered
	/// and when the rig appears, and it is never written again. The rig reads the part directly
	/// (the part <b>is</b> the IK target), so there is no per-frame copy of the tracked poses and
	/// no <c>LateUpdate</c> to schedule.
	/// </para>
	/// </summary>
	public partial class XRControllerV2 {
		/// <summary>Rig provider of the loaded avatar (the module owning the live rig).</summary>
		private IRigProvider _rig;

		/// <summary>Parts bound to the rig, so they can be unbound when they go away.</summary>
		private readonly Dictionary<Part, ushort> _boundParts = new();

		/// <summary>
		/// Rig that currently holds the bindings: the parts must be released on <b>this</b> rig,
		/// not on the one the provider exposes (a hot-swap already replaced it).
		/// </summary>
		private IRigging _boundRig;

		/// <summary>Parts the rig does not carry: said once, then remembered.</summary>
		private readonly HashSet<ushort> _missingParts = new();

		/* ------------------------------------------------------------------ */
		/* Rig                                                                 */
		/* ------------------------------------------------------------------ */

		/// <summary>
		/// Binds the rig of the avatar.
		/// <para>
		/// The rig lives in a module of the avatar: it appears when the module set changes and it may be
		/// replaced by a rig hot-swap. The descriptor announces it, so the provider is never polled.
		/// </para>
		/// </summary>
		private void BindRigProvider(IAvatarDescriptor descriptor) {
			if (descriptor == null)
				return;

			descriptor.OnModuleRegistered.AddListener(OnModuleRegistered);
			descriptor.OnModuleUnregistered.AddListener(OnModuleUnregistered);

			var providers = descriptor.GetModules<IRigProvider>();
			if (providers.Length > 0)
				OnModuleRegistered(providers[0]);
		}

		/// <summary>Stops listening to the avatar.</summary>
		private void UnbindRigProvider() {
			var descriptor = RigDescriptor;
			if (descriptor != null) {
				descriptor.OnModuleRegistered.RemoveListener(OnModuleRegistered);
				descriptor.OnModuleUnregistered.RemoveListener(OnModuleUnregistered);
			}

			UnbindAllParts();

			_rig?.OnRigChanged.RemoveListener(OnRigChanged);
			_rig = null;
		}

		/// <summary>
		/// Descriptor of the loaded avatar: the one the rig provider is bound to.
		/// <para>
		/// Read from the avatar itself rather than cached: the controller already holds the avatar, so the
		/// descriptor is one property away and can never drift from it.
		/// </para>
		/// </summary>
		private IAvatarDescriptor RigDescriptor
			=> _avatar?.Descriptor;

		/// <summary>A module joined the avatar: it may be the rig provider.</summary>
		private void OnModuleRegistered(IAvatarModule module) {
			if (module is not IRigProvider provider)
				return;

			if (_rig is not null && !ReferenceEquals(_rig, provider))
				_rig.OnRigChanged.RemoveListener(OnRigChanged);

			_rig = provider;

			provider.OnRigChanged.AddListener(OnRigChanged);
			OnRigChanged(provider.Rig);
		}

		/// <summary>A module left the avatar: it may have been the rig provider.</summary>
		private void OnModuleUnregistered(IAvatarModule module) {
			if (module is not IRigProvider provider || !ReferenceEquals(provider, _rig))
				return;

			provider.OnRigChanged.RemoveListener(OnRigChanged);
			UnbindAllParts();
			_rig = null;
		}

		/* ------------------------------------------------------------------ */
		/* Binding                                                            */
		/* ------------------------------------------------------------------ */

		/// <summary>
		/// The rig was created, replaced or disposed: the parts must be bound again on the new
		/// targets, and released on the old ones.
		/// </summary>
		private void OnRigChanged(IRigging rig) {
			UnbindAllParts();
			if (rig == null)
				return;
			rig.SetExternalRootControl(true);
			BindParts(rig);
		}

		/// <summary>
		/// Binds every part of the player to the rig slot it drives.
		/// <para>
		/// The part is bound to the rig as its <b>target</b>: the solver reads the part directly,
		/// so the tracked pose reaches the bones without a per-frame copy.
		/// </para>
		/// </summary>
		private void BindParts(IRigging rig) {
			if (!player)
				return;

			foreach (var pair in player.Parts) {
				var part = pair.Value;
				if (!part)
					continue;

				BindPart(rig, part, pair.Key);
			}
		}

		/// <summary>
		/// Binds a part to the rig slot it drives.
		/// </summary>
		/// <param name="rig">Rig of the avatar.</param>
		/// <param name="part">Part driving the slot.</param>
		/// <param name="rigSlot">Slot the part occupies.</param>
		private void BindPart(IRigging rig, Part part, EntityRig rigSlot) {
			var id = rigSlot.ToIndex();

			if (id == EntityRig.Base.ToIndex())
				return;

			var target = part.RigTarget;
			if (!target)
				return;

			if (!rig.SetTarget(id, target)) {
				if (_missingParts.Add(id))
					Logger.LogWarning(
						$"No IK target for {rigSlot} on the rig: this part will not drive the avatar.",
						this, tag: nameof(XRControllerV2));

				return;
			}

			_missingParts.Remove(id);
			_boundParts[part] = id;
			_boundRig = rig;

			Logger.LogDebug($"[XR] bound {rigSlot} → {part.name} (enabled: {part.PartEnabled})", this, tag: nameof(XRControllerV2));

			part.EnabledChanged.RemoveListener(OnBoundPartEnabledChanged);
			part.EnabledChanged.AddListener(OnBoundPartEnabledChanged);
			SetActive(rig, id.ToEntityRig(), part.PartEnabled);
		}

		/// <summary>
		/// Turns the IK of a rig slot on or off: the controller channel of the rig, which the avatar's own
		/// <c>TrackingControl</c> overrides when it has an opinion on the bone.
		/// </summary>
		private void SetActive(IRigging rig, EntityRig slotRig, bool enabled) {
			Logger.LogDebug($"[XR] SetActive {slotRig} = {enabled} on rig '{rig?.Id ?? "none"}'", this, tag: nameof(XRControllerV2));

			rig?.SetActive(slotRig.ToHumanBodyBones(), enabled);
		}

		/// <summary>A bound part was frozen (follower) or released: hand the slot back to the animation.</summary>
		private void OnBoundPartEnabledChanged(Part part, bool enabled) {
			if (_boundRig == null || !part || !_boundParts.TryGetValue(part, out var id))
				return;

			SetActive(_boundRig, id.ToEntityRig(), enabled);
		}

		/// <summary>Releases every part bound to the rig.</summary>
		private void UnbindAllParts() {
			if (_boundParts.Count == 0) {
				_boundRig = null;
				return;
			}

			foreach (var pair in _boundParts)
				UnbindPart(pair.Key, pair.Value);

			_boundParts.Clear();
			_boundRig = null;
		}

		/// <summary>
		/// Releases a part from its rig slot: the solver goes back to the target it generated, and the slot is
		/// handed back to the animation.
		/// </summary>
		private void UnbindPart(Part part, ushort id) {
			if (part)
				part.EnabledChanged.RemoveListener(OnBoundPartEnabledChanged);

			_boundRig?.SetTarget(id, null);
			SetActive(_boundRig, id.ToEntityRig(), false);
		}

		/// <summary>
		/// A part joined the player: it is bound to the rig slot it drives, if the rig carries it.
		/// </summary>
		private void OnPartRegistered(Part part) {
			if (_avatar != null) {
				var descriptor = _avatar.Descriptor;

				ApplyTrackingParameters(descriptor);

				// A hand registered after the avatar needs its pivot (offset, rotation) and its finger source:
				// without them it sits on the raw controller pose and no finger follows the avatar hand.
				if (part is Hand hand)
					ApplyHandOffset(descriptor, hand);
			}

			var rig = _boundRig;
			if (rig == null || !part)
				return;
			BindPart(rig, part, part.Rig);
		}

		/// <summary>
		/// A part left the player: the rig slot it drove goes back to its generated target.
		/// </summary>
		private void OnPartUnregistered(Part part) {
			if (!part || !_boundParts.TryGetValue(part, out var id))
				return;

			UnbindPart(part, id);
			_boundParts.Remove(part);

			if (_avatar != null)
				ApplyTrackingParameters(_avatar.Descriptor);
		}
	}
}

using UnityEngine;

namespace Nox.XR.Controller.V2 {
	/// <summary>
	/// Keeps the avatar container under the head of the player, in the frame of the tracking space, <b>before</b> the
	/// IK solves.
	/// <para>
	/// FinalIK solves in <c>LateUpdate</c> at execution order 0, from hand targets that live in the tracking space
	/// next to the avatar container (the container is a child of the tracking space, the hand parts are siblings of
	/// it, not its children). Moving the container <b>after</b> the solve - the controller runs at order 15 -
	/// displaced the whole armature once the arms had been solved, so the hands carried the move of the frame: a
	/// visible offset between the solved pose and the moved one, worst during a space drag.
	/// </para>
	/// <para>
	/// At order -100 the container is placed with the pose of the current frame (the head is written in
	/// <c>Update</c>), and the solver then reaches it.
	/// </para>
	/// </summary>
	[AddComponentMenu("Nox/XR/Player/Avatar Follower")]
	[DefaultExecutionOrder(-100)]
	public class AvatarFollower : MonoBehaviour {
		private Nox.XR.Player.Player _player;
		private Transform _container;

		/// <summary>Player whose head the avatar follows, and the container the avatar is parented to.</summary>
		public void Bind(Nox.XR.Player.Player player, Transform container) {
			_player = player;
			_container = container;
		}

		private void LateUpdate() {
			if (!_player || !_container || _container.childCount == 0)
				return;

			var space = _player.TrackingContainer;
			if (!space)
				return;

			var head = _player.Head;
			var source = head ? head.Position : _player.transform.position;
			var pose = _player.transform;

			var local = space.InverseTransformPoint(new Vector3(source.x, space.position.y, source.z));
			var current = _container.localPosition;

			_container.localPosition = new Vector3(local.x, current.y, local.z);
			_container.localRotation = Quaternion.Inverse(space.rotation) * pose.rotation;
		}
	}
}

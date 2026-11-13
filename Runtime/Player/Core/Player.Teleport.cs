using UnityEngine;

namespace Nox.XR.Player {
	public partial class Player {
		/* ------------------------------------------------------------------ */
		/* Teleport / rotation                                                 */
		/* ------------------------------------------------------------------ */

		public virtual void SetPosition(Vector3 position) 
            => SetPosition(position, Head ? Head.View.rotation : transform.rotation);

		public virtual void SetPosition(Vector3 position, Quaternion rotation) {
			var deltaPos = position - transform.position;
			transform.position += deltaPos;

			var targetPos = transform.position - Head.Position;
			targetPos.y = deltaPos.y;
			trackingContainer.position += targetPos;

			_lastUpdatePosition = transform.position;
			_targetTrackedPos = new Vector3(trackingContainer.position.x, _targetTrackedPos.y + deltaPos.y, trackingContainer.position.z);
			body.position = transform.position;

			_lastHeadPos = Head.Position;

			SafeMoveHandToPosition(RightHand, transform, RightHand ? RightHand.transform.position : Vector3.zero);
			SafeMoveHandToPosition(LeftHand, transform, LeftHand ? LeftHand.transform.position : Vector3.zero);

			AddRotation(rotation * Quaternion.Inverse(Head.View.rotation));
			NotifyTeleport();
		}

		public virtual void SetRotation(Quaternion rotation) {
			var targetPos = transform.position - Head.Position;
			targetPos.y = 0f;
			trackingContainer.position += targetPos;
			_lastUpdatePosition = transform.position;

			var deltaRot = rotation * Quaternion.Inverse(Head.View.rotation);
			trackingContainer.RotateAround(Head.Position, Vector3.up, deltaRot.eulerAngles.y);

			_targetTrackedPos = new Vector3(trackingContainer.position.x, _targetTrackedPos.y, trackingContainer.position.z);

			SafeMoveHandToPosition(RightHand, transform, RightHand ? RightHand.transform.position : Vector3.zero);
			SafeMoveHandToPosition(LeftHand, transform, LeftHand ? LeftHand.transform.position : Vector3.zero);

			if (deltaRot.eulerAngles.magnitude > 10f)
				NotifyTeleport();
		}

		public virtual void AddRotation(Quaternion addRotation) {
			var targetPos = transform.position - Head.Position;
			targetPos.y = 0f;
			trackingContainer.position += targetPos;
			_lastUpdatePosition = transform.position;

			trackingContainer.RotateAround(Head.Position, Vector3.up, addRotation.eulerAngles.y);

			_targetTrackedPos = new Vector3(trackingContainer.position.x, _targetTrackedPos.y, trackingContainer.position.z);

			SafeMoveHandToPosition(RightHand, transform, RightHand ? RightHand.transform.position : Vector3.zero);
			SafeMoveHandToPosition(LeftHand, transform, LeftHand ? LeftHand.transform.position : Vector3.zero);

			if (addRotation.eulerAngles.magnitude > 10f)
				NotifyTeleport();
		}

		public virtual void Recenter() {
			var targetPos = transform.position - Head.Position;
			targetPos.y = 0f;

			trackingContainer.position += targetPos;
			_lastUpdatePosition = transform.position;

			_targetTrackedPos = new Vector3(trackingContainer.position.x, _targetTrackedPos.y, trackingContainer.position.z);
		}

		private void NotifyTeleport() {
			foreach (var part in _parts.Values)
				part.OnPlayerTeleport();

			OnTeleported?.Invoke(this);
		}

		private void SafeMoveHandToPosition(Hand hand, Transform playerTransform, Vector3 desiredHandPosition) {
			if (!hand || !hand.Body)
				return;

			var handBody = hand.Body;
			var bodyXZCenter = new Vector3(playerTransform.position.x, handBody.position.y, playerTransform.position.z);

			handBody.position = bodyXZCenter;
			handBody.transform.position = bodyXZCenter;

			var offset = desiredHandPosition - bodyXZCenter;
			var distance = offset.magnitude;
			if (distance < 0.0001f)
				return;

			var direction = offset.normalized;
			if (handBody.SweepTest(direction, out var hit, distance))
				hand.SetHandLocation(bodyXZCenter + direction * hit.distance);
			else
				hand.SetHandLocation(desiredHandPosition);
		}
	}
}

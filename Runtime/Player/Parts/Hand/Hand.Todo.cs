using System;
using Nox.Interactions;

namespace Nox.XR.Player {
	/// <summary>
	/// Grab press of a hand that already holds something (AutoHand's <c>GrabLock</c>): the press is reported instead of
	/// being dropped, so a prop such as a lever or a trigger subscribes to it and acts instead of the hand refusing
	/// silently.
	/// </summary>
	public partial class Hand {
		/// <summary>
		/// Raised when the grab input is pressed while this hand already holds something: a prop that drives itself from
		/// the press of the hand carrying it subscribes here.
		/// </summary>
		public event Action<IGrabbable> OnGrabPressed;

		private void RaiseGrabPressed() {
			if (_held != null)
				OnGrabPressed?.Invoke(_held);
		}
	}
}

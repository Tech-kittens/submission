# Phone meeting voice commands

## Confirmed backend limitation

The phone UI supports standalone meetings through `POST /api/meetings/start`
without a device ID and uploads PCM through
`POST /api/meetings/{id}/audio/segments`.

However, `Quacktion.App/Services/MeetingToolService.cs` checks
`session.DeviceId is not null` in both `start_meeting` and `end_meeting` and
returns `device_required` for account-only phone sessions. These branches call
the device meeting lifecycle, rather than the mobile meeting lifecycle.
`GeminiTools.cs` also describes end_meeting as operating on a device.

This explains how a phone can support meetings through the UI while the voice
agent reports that the device is unsupported. The user's individual tool result
has not been inspected; its exact error is not confirmed.

## Proposed backend/client integration

Keep the existing device behavior. For phone sessions, expose a durable,
authenticated phone meeting intent that the owning client can observe and
acknowledge. Starting must use the phone consent UI and available microphone,
associate the returned meeting ID with the logical conversation, and establish
capture/upload before confirming success to the agent. A backend-only call to
StartMobileAsync does not start the phone microphone or its uploader.

For phone end requests, let the owning client flush and acknowledge every buffered
segment before calling the meeting end route. The tool must not finalize the
meeting while the phone still has audio pending. Report pending, unavailable,
and confirmed states accurately; never describe all phone meetings as unsupported.

## Verification needed

Test spoken start/end for phone and hardware sessions; consent declined; no active
client/microphone; duplicate calls; account ownership; session resume; upload
failure; buffered final audio; and recovery after client disconnect. Confirm phone
voice commands create no device-mode requests and preserve the conversation ID.

Until this integration exists, use the app's Meeting mode and End meeting buttons
for phone meetings. Backend files were inspected read-only; none were changed.

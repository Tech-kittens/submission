import 'package:flutter/material.dart';
import 'package:quacktion_frontend/features/meetings/application/meeting_mode_controller.dart';

class MeetingModeControl extends StatefulWidget {
  const MeetingModeControl({
    super.key,
    required this.controller,
    required this.sessionId,
    required this.name,
    required this.canStart,
  });
  final MeetingModeController controller;
  final String sessionId, name;
  final bool canStart;

  @override
  State<MeetingModeControl> createState() => _MeetingModeControlState();
}

class _MeetingModeControlState extends State<MeetingModeControl> {
  @override
  void initState() {
    super.initState();
    widget.controller.watch(widget.sessionId);
  }

  @override
  void didUpdateWidget(covariant MeetingModeControl oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (oldWidget.controller != widget.controller) {
      oldWidget.controller.watch(null);
    }
    widget.controller.watch(widget.sessionId);
  }

  @override
  void dispose() {
    widget.controller.watch(null);
    super.dispose();
  }

  Future<void> _start() => showModalBottomSheet<void>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    showDragHandle: true,
    builder: (_) => _MeetingConsent(
      controller: widget.controller,
      sessionId: widget.sessionId,
      name: widget.name,
    ),
  );

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) {
      final controller = widget.controller;
      final id = widget.sessionId;
      final meeting = controller.meeting(id);
      final stale = controller.stale(id);
      final busy = controller.busy(id);
      final error = controller.error(id);
      // Phone capture is local. Server transcription/recording metadata may lag
      // while the accepted microphone frames are still buffered for upload.
      final recording = meeting?.canEnd == true && controller.capturing(id);
      final paused = meeting?.canEnd == true && !recording && error != null;
      final label = controller.uploadIssue(id) && recording
          ? 'Meeting recording · Retrying upload'
          : stale
          ? 'Meeting status unavailable'
          : busy && meeting?.canEnd == true
          ? 'Saving meeting audio…'
          : paused
          ? 'Meeting recording paused'
          : meeting?.canEnd == true && !recording
          ? 'Starting meeting mode…'
          : recording
          ? 'Meeting mode on · Recording'
          : meeting?.label ?? '';
      final showState = meeting != null || stale;
      return Padding(
        padding: const EdgeInsets.fromLTRB(16, 8, 16, 8),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (showState)
              Semantics(
                liveRegion: true,
                child: Container(
                  padding: const EdgeInsets.all(14),
                  decoration: BoxDecoration(
                    color: const Color(0xFFFFF2CF),
                    borderRadius: BorderRadius.circular(16),
                    border: Border.all(color: const Color(0xFFE5BE49)),
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Icon(
                            stale
                                ? Icons.warning_amber_rounded
                                : recording
                                ? Icons.fiber_manual_record
                                : Icons.groups_rounded,
                            color: !stale && recording
                                ? const Color(0xFFB3261E)
                                : const Color(0xFF765B00),
                            size: 20,
                          ),
                          const SizedBox(width: 10),
                          Expanded(
                            child: Text(
                              label,
                              style: const TextStyle(
                                fontWeight: FontWeight.w700,
                              ),
                            ),
                          ),
                        ],
                      ),
                      const SizedBox(height: 6),
                      Text(
                        stale
                            ? 'Recording state could not be confirmed.'
                            : recording
                            ? 'Recording with your phone microphone. Audio is uploaded for transcription. Say “Hey Quacktion” or use Ask assistant below when you have a question.'
                            : meeting?.processing == true
                            ? 'Normal conversation can continue while the report is prepared.'
                            : controller.uploadIssue(id)
                            ? 'Buffered audio is waiting to be sent. The meeting remains active until you end it.'
                            : meeting?.canEnd == true
                            ? 'Waiting for microphone audio. The meeting remains active until you end it.'
                            : meeting?.status == 'Completed'
                            ? 'Recording has ended.'
                            : 'Check the meeting status before continuing.',
                      ),
                      if (controller.canRetryUpload(id))
                        Align(
                          alignment: Alignment.centerRight,
                          child: TextButton.icon(
                            onPressed: () => controller.retryUpload(id),
                            icon: const Icon(Icons.cloud_upload_outlined),
                            label: const Text('Retry audio upload'),
                          ),
                        ),
                      if (meeting?.canEnd == true)
                        Align(
                          alignment: Alignment.centerRight,
                          child: TextButton.icon(
                            onPressed: busy ? null : () => controller.end(id),
                            icon: const Icon(Icons.stop_circle_outlined),
                            label: Text(
                              busy
                                  ? 'Uploading audio…'
                                  : controller.uploadIssue(id)
                                  ? 'Retry upload and end meeting'
                                  : 'End meeting',
                            ),
                          ),
                        ),
                    ],
                  ),
                ),
              ),
            if (error != null)
              Padding(
                padding: const EdgeInsets.only(top: 8),
                child: Text(error),
              ),
            Wrap(
              alignment: WrapAlignment.end,
              children: [
                if (error != null || showState)
                  TextButton(
                    onPressed: busy ? null : () => controller.refresh(id),
                    child: const Text('Refresh meeting status'),
                  ),
                if (meeting == null || meeting.terminal)
                  TextButton.icon(
                    onPressed: widget.canStart && !busy && !stale
                        ? _start
                        : null,
                    icon: const Icon(Icons.groups_outlined),
                    label: const Text('Meeting mode'),
                  ),
              ],
            ),
            if (!widget.canStart && meeting == null)
              const Text(
                'Start a voice conversation to use meeting mode.',
                textAlign: TextAlign.right,
                style: TextStyle(fontSize: 12, color: Color(0xFF69685F)),
              ),
          ],
        ),
      );
    },
  );
}

class _MeetingConsent extends StatefulWidget {
  const _MeetingConsent({
    required this.controller,
    required this.sessionId,
    required this.name,
  });
  final MeetingModeController controller;
  final String sessionId, name;
  @override
  State<_MeetingConsent> createState() => _MeetingConsentState();
}

class _MeetingConsentState extends State<_MeetingConsent> {
  late String _name = widget.name;
  String _type = 'General';
  bool _consent = false;
  Future<void> _start() async {
    final success = await widget.controller.start(
      widget.sessionId,
      name: _name.trim(),
      type: _type,
      consent: _consent,
    );
    if (success && mounted) Navigator.pop(context);
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.controller,
    builder: (context, _) {
      final busy = widget.controller.busy(widget.sessionId);
      return SingleChildScrollView(
        padding: EdgeInsets.fromLTRB(
          24,
          8,
          24,
          24 + MediaQuery.viewInsetsOf(context).bottom,
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          mainAxisSize: MainAxisSize.min,
          children: [
            Text(
              'Start meeting mode',
              style: Theme.of(context).textTheme.headlineSmall,
            ),
            const SizedBox(height: 12),
            const Text(
              'Your conversation will pause while your phone microphone is recorded and transcribed to prepare a meeting report. Say “Hey Quacktion” during recording to ask a question, then use Back to meeting to pause the assistant again. Inform everyone and obtain their consent before continuing.',
            ),
            const SizedBox(height: 16),
            TextFormField(
              initialValue: _name,
              maxLength: 200,
              enabled: !busy,
              decoration: const InputDecoration(labelText: 'Meeting name'),
              onChanged: (value) => setState(() => _name = value),
            ),
            DropdownButtonFormField<String>(
              initialValue: _type,
              decoration: const InputDecoration(labelText: 'Meeting type'),
              items: const [
                DropdownMenuItem(value: 'General', child: Text('General')),
                DropdownMenuItem(value: 'Standup', child: Text('Standup')),
                DropdownMenuItem(value: 'Planning', child: Text('Planning')),
                DropdownMenuItem(
                  value: 'Brainstorming',
                  child: Text('Brainstorming'),
                ),
                DropdownMenuItem(
                  value: 'DecisionMaking',
                  child: Text('Decision making'),
                ),
                DropdownMenuItem(
                  value: 'Retrospective',
                  child: Text('Retrospective'),
                ),
                DropdownMenuItem(
                  value: 'ClientMeeting',
                  child: Text('Client meeting'),
                ),
                DropdownMenuItem(
                  value: 'Presentation',
                  child: Text('Presentation'),
                ),
                DropdownMenuItem(value: 'Interview', child: Text('Interview')),
              ],
              onChanged: busy
                  ? null
                  : (value) => setState(() => _type = value ?? _type),
            ),
            const SizedBox(height: 12),
            CheckboxListTile(
              contentPadding: EdgeInsets.zero,
              value: _consent,
              title: const Text(
                'I confirm everyone has been informed and consent to recording and transcription has been obtained.',
              ),
              onChanged: busy
                  ? null
                  : (value) => setState(() => _consent = value == true),
            ),
            if (widget.controller.error(widget.sessionId)
                case final String error)
              Padding(
                padding: const EdgeInsets.only(bottom: 12),
                child: Text(error),
              ),
            FilledButton.icon(
              onPressed: _consent && _name.trim().isNotEmpty && !busy
                  ? _start
                  : null,
              icon: const Icon(Icons.fiber_manual_record),
              label: Text(busy ? 'Starting…' : 'Start meeting'),
            ),
            TextButton(
              onPressed: busy ? null : () => Navigator.pop(context),
              child: const Text('Cancel'),
            ),
          ],
        ),
      );
    },
  );
}

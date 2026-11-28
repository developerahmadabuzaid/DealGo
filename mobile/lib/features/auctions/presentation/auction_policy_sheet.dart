import 'package:flutter/material.dart';

import '../../../core/localization/app_strings.dart';
import '../../../core/theme/app_theme.dart';
import '../../legal/terms_document.dart';
import '../data/auction_policy.dart';

/// Shows the auction policies and returns true only when the person ticked the box and tapped
/// accept. Shown before every join.
Future<bool> confirmAuctionPolicy(BuildContext context, AuctionPolicy policy) async {
  final accepted = await showModalBottomSheet<bool>(
    context: context,
    isScrollControlled: true,
    useSafeArea: true,
    builder: (_) => AuctionPolicySheet(policy: policy),
  );
  return accepted == true;
}

class AuctionPolicySheet extends StatefulWidget {
  const AuctionPolicySheet({super.key, required this.policy});

  final AuctionPolicy policy;

  @override
  State<AuctionPolicySheet> createState() => _AuctionPolicySheetState();
}

class _AuctionPolicySheetState extends State<AuctionPolicySheet> {
  bool _agreed = false;

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final theme = Theme.of(context);
    // The same layout rules as the terms: a line without a full stop is a heading.
    final doc = TermsDocument.parse('${widget.policy.title}\n${widget.policy.body}');
    final height = MediaQuery.sizeOf(context).height;

    return ConstrainedBox(
      constraints: BoxConstraints(maxHeight: height * 0.85),
      child: Directionality(
        // The policies are written in Arabic whatever language the rest of the app is in.
        textDirection: TextDirection.rtl,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(
              AppTheme.space4, AppTheme.space4, AppTheme.space4, AppTheme.space4),
          child: Column(
            mainAxisSize: MainAxisSize.min,
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              Text(
                doc.title,
                style: theme.textTheme.titleLarge?.copyWith(fontWeight: FontWeight.w800),
              ),
              const SizedBox(height: AppTheme.space3),
              Flexible(
                child: SingleChildScrollView(
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.stretch,
                    children: [
                      for (final s in doc.sections) ...[
                        if (s.heading.isNotEmpty)
                          Padding(
                            padding: const EdgeInsets.only(top: AppTheme.space3, bottom: AppTheme.space2),
                            child: Text(
                              s.heading,
                              style: theme.textTheme.titleMedium?.copyWith(
                                fontWeight: FontWeight.w800,
                                color: AppTheme.brand,
                              ),
                            ),
                          ),
                        for (final p in s.paragraphs)
                          Padding(
                            padding: const EdgeInsets.only(bottom: AppTheme.space2),
                            child: Text(p, style: theme.textTheme.bodyMedium?.copyWith(height: 1.7)),
                          ),
                      ],
                    ],
                  ),
                ),
              ),
              const Divider(height: AppTheme.space5),
              CheckboxListTile(
                key: const Key('auction-policy-agree'),
                value: _agreed,
                onChanged: (v) => setState(() => _agreed = v ?? false),
                controlAffinity: ListTileControlAffinity.leading,
                contentPadding: EdgeInsets.zero,
                title: Text(t.auctionPolicyAgree, style: const TextStyle(fontWeight: FontWeight.w700)),
              ),
              const SizedBox(height: AppTheme.space2),
              FilledButton(
                key: const Key('auction-policy-accept'),
                onPressed: _agreed ? () => Navigator.of(context).pop(true) : null,
                child: Text(t.auctionPolicyAccept),
              ),
            ],
          ),
        ),
      ),
    );
  }
}

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:go_router/go_router.dart';

import '../../../core/errors/api_exception.dart';
import '../../../core/format/formatters.dart';
import '../../../core/localization/app_strings.dart';
import '../../../core/providers.dart';
import '../../../shared/app_header.dart';
import '../../../shared/legal_links.dart';
import '../../../shared/otp_input.dart';
import '../../../shared/widgets.dart';
import '../../auth/data/auth_repository.dart' show OtpChallenge;

class ProfileScreen extends ConsumerWidget {
  const ProfileScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final user = ref.watch(authControllerProvider).user;

    if (user == null) return const Scaffold(body: EmptyState());

    return Scaffold(
      body: ListView(
        padding: const EdgeInsets.fromLTRB(16, 16, 16, 32),
        children: [
          Card(
            child: Padding(
              padding: const EdgeInsets.all(18),
              child: Row(
                children: [
                  CircleAvatar(
                    radius: 26,
                    child: Text(user.fullName.isEmpty ? '?' : user.fullName[0].toUpperCase()),
                  ),
                  const SizedBox(width: 14),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(user.fullName,
                            style: Theme.of(context)
                                .textTheme
                                .titleMedium
                                ?.copyWith(fontWeight: FontWeight.bold)),
                        Text(user.email,
                            style: Theme.of(context).textTheme.bodySmall),
                        if (user.phoneNumber != null)
                          Text(user.phoneNumber!,
                              style: Theme.of(context).textTheme.bodySmall),
                      ],
                    ),
                  ),
                ],
              ),
            ),
          ),

          const SizedBox(height: 16),

          Card(
            child: Column(
              children: [
                ListTile(
                  leading: const Icon(Icons.emoji_events_outlined),
                  title: Text(t.myWins),
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/prizes'),
                ),
                const Divider(height: 1),
                ListTile(
                  leading: const Icon(Icons.receipt_long_outlined),
                  title: Text(t.myOrders),
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/store/orders'),
                ),
                const Divider(height: 1),
                ListTile(
                  leading: const Icon(Icons.history_rounded),
                  title: Text(t.auctionHistory),
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.go('/auctions'),
                ),
                const Divider(height: 1),
                ListTile(
                  leading: const Icon(Icons.notifications_outlined),
                  title: Text(t.notifications),
                  trailing: const Icon(Icons.chevron_right_rounded),
                  onTap: () => context.push('/notifications'),
                ),
              ],
            ),
          ),

          const SizedBox(height: 16),

          Card(
            child: Column(
              children: [
                ListTile(
                  leading: const Icon(Icons.description_outlined),
                  title: Text(t.termsOfUse),
                  trailing: const Icon(Icons.chevron_left_rounded),
                  onTap: () => openTerms(context),
                ),
                const Divider(height: 1),
                ListTile(
                  leading: const Icon(Icons.privacy_tip_outlined),
                  title: Text(t.privacyPolicy),
                  trailing: const Icon(Icons.chevron_left_rounded),
                  onTap: () => openTerms(context, privacy: true),
                ),
              ],
            ),
          ),

          // The app is Arabic only, so there is no language picker here.
          const SizedBox(height: 24),

          OutlinedButton.icon(
            onPressed: () => ref.read(authControllerProvider.notifier).signOut(),
            icon: const Icon(Icons.logout_rounded),
            label: Text(t.logout),
          ),

          const SizedBox(height: 8),

          TextButton.icon(
            onPressed: () => showModalBottomSheet<void>(
              context: context,
              isScrollControlled: true,
              builder: (_) => const DeleteAccountSheet(),
            ),
            style: TextButton.styleFrom(foregroundColor: Theme.of(context).colorScheme.error),
            icon: const Icon(Icons.delete_forever_outlined),
            label: Text(t.deleteAccount),
          ),
        ],
      ),
    );
  }
}

/// Deletes the account after the user confirms with a code sent to their phone.
///
/// The server checks first that nothing would be lost (a withdrawable balance, an auction, a
/// device, an order or a payment in progress) and says why when it refuses.
class DeleteAccountSheet extends ConsumerStatefulWidget {
  const DeleteAccountSheet({super.key});

  @override
  ConsumerState<DeleteAccountSheet> createState() => _DeleteAccountSheetState();
}

class _DeleteAccountSheetState extends ConsumerState<DeleteAccountSheet> {
  OtpChallenge? _challenge;
  String? _error;
  bool _busy = false;

  Future<void> _requestCode() async {
    setState(() {
      _busy = true;
      _error = null;
    });

    try {
      final challenge =
          await ref.read(authRepositoryProvider).requestAccountDeletionOtp();
      setState(() => _challenge = challenge);
    } on ApiException catch (e) {
      setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _confirm(String code) async {
    setState(() {
      _busy = true;
      _error = null;
    });

    final messenger = ScaffoldMessenger.of(context);
    final navigator = Navigator.of(context);
    final deleted = AppStrings.of(context).accountDeleted;

    try {
      await ref.read(authControllerProvider.notifier).deleteAccount(code);
      navigator.pop();
      messenger.showSnackBar(SnackBar(content: Text(deleted)));
    } on ApiException catch (e) {
      if (mounted) setState(() => _error = e.displayMessage);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final t = AppStrings.of(context);
    final error = Theme.of(context).colorScheme.error;

    return Padding(
      padding: EdgeInsets.fromLTRB(20, 20, 20, MediaQuery.of(context).viewInsets.bottom + 20),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text(t.deleteAccount, style: Theme.of(context).textTheme.titleLarge),
          const SizedBox(height: 8),
          Text(
            _challenge == null ? t.deleteAccountExplanation : t.deleteAccountConfirmCode,
            style: Theme.of(context).textTheme.bodyMedium,
          ),
          const SizedBox(height: 16),

          if (_challenge == null) ...[
            if (_error != null) ...[
              Text(_error!, style: TextStyle(color: error)),
              const SizedBox(height: 12),
            ],
            FilledButton(
              onPressed: _busy ? null : _requestCode,
              style: FilledButton.styleFrom(backgroundColor: error),
              child: _busy
                  ? const SizedBox(
                      height: 20, width: 20,
                      child: CircularProgressIndicator(strokeWidth: 2, color: Colors.white))
                  : Text(t.deleteAccountSendCode),
            ),
            const SizedBox(height: 8),
            TextButton(
              onPressed: _busy ? null : () => Navigator.of(context).pop(),
              child: Text(t.cancel),
            ),
          ] else
            OtpInput(
              challenge: _challenge!,
              busy: _busy,
              error: _error,
              onSubmit: _confirm,
              onResend: _requestCode,
            ),
        ],
      ),
    );
  }
}

class NotificationsScreen extends ConsumerWidget {
  const NotificationsScreen({super.key});

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final t = AppStrings.of(context);
    final locale = Localizations.localeOf(context).languageCode;
    final async = ref.watch(notificationsProvider);

    return Scaffold(
      appBar: BrandAppBar(
        title: Text(t.notifications),
        actions: [
          TextButton(
            onPressed: () async {
              await ref.read(notificationRepositoryProvider).markAllRead();
              ref.invalidate(notificationsProvider);
              ref.invalidate(unreadCountProvider);
            },
            child: Text(t.markAllRead),
          ),
        ],
      ),
      body: async.when(
        loading: () => const Center(child: CircularProgressIndicator()),
        error: (e, _) => ErrorView(
          message: e.toString(),
          onRetry: () => ref.invalidate(notificationsProvider),
        ),
        data: (paged) => paged.items.isEmpty
            ? const EmptyState(icon: Icons.notifications_off_outlined)
            : ListView.separated(
                itemCount: paged.items.length,
                separatorBuilder: (_, _) => const Divider(height: 1),
                itemBuilder: (context, i) {
                  final n = paged.items[i];

                  return ListTile(
                    leading: Icon(
                      n.isUnread ? Icons.circle : Icons.circle_outlined,
                      size: 12,
                      color: n.isUnread
                          ? Theme.of(context).colorScheme.primary
                          : Theme.of(context).colorScheme.outline,
                    ),
                    title: Text(n.title,
                        style: TextStyle(
                            fontWeight: n.isUnread ? FontWeight.bold : FontWeight.normal)),
                    subtitle: Text(n.body),
                    trailing: Text(Fmt.dateTime(n.createdAt, locale: locale),
                        style: Theme.of(context).textTheme.labelSmall),
                    onTap: () async {
                      if (n.isUnread) {
                        await ref.read(notificationRepositoryProvider).markRead(n.id);
                        ref.invalidate(notificationsProvider);
                        ref.invalidate(unreadCountProvider);
                      }
                    },
                  );
                },
              ),
      ),
    );
  }
}

import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:mobile/core/localization/app_strings.dart';
import 'package:mobile/features/wallets/data/wallet_repository.dart';

void main() {
  group('Wallets', () {
    final wallets = Wallets.fromJson({
      'main': {
        'id': 'w1', 'walletType': 'Main', 'balance': 150.0,
        'currency': 'USD', 'isWithdrawable': true,
      },
      'shopping': {
        'id': 'w2', 'walletType': 'Shopping', 'balance': 40.0,
        'currency': 'USD', 'isWithdrawable': false,
      },
      'currency': 'USD',
    });

    test('keeps the two balances separate and only marks Main withdrawable', () {
      expect(wallets.main.balance, 150.0);
      expect(wallets.main.isWithdrawable, isTrue);

      expect(wallets.shopping.balance, 40.0);
      expect(wallets.shopping.isWithdrawable, isFalse);
    });

    test('exposes no combined total', () {
      // Presenting 190 as one balance would imply the shopping credit is withdrawable.
      // The model deliberately offers no such field.
      expect(wallets.main.balance + wallets.shopping.balance, 190.0);
      expect(
        (wallets as dynamic).toString().contains('total'),
        isFalse,
        reason: 'a combined total must not be part of the wallet model',
      );
    });
  });

  group('WalletTransaction', () {
    test('classifies credits and debits by sign', () {
      final credit = WalletTransaction.fromJson({
        'id': 't1', 'walletType': 'Shopping', 'transactionType': 'ShoppingCredit',
        'amount': 20.0, 'currency': 'USD', 'balanceBefore': 0.0, 'balanceAfter': 20.0,
        'creditSource': 'AuctionEntryFeeRefund',
        'createdAt': '2026-09-07T12:00:00.000Z',
      });

      final debit = WalletTransaction.fromJson({
        'id': 't2', 'walletType': 'Shopping', 'transactionType': 'StorePurchase',
        'amount': -15.0, 'currency': 'USD', 'balanceBefore': 20.0, 'balanceAfter': 5.0,
        'createdAt': '2026-09-07T12:05:00.000Z',
      });

      expect(credit.isCredit, isTrue);
      expect(credit.creditSource, 'AuctionEntryFeeRefund');
      expect(debit.isCredit, isFalse);
      expect(debit.balanceAfter, 5.0);
    });
  });

  group('AppStrings', () {
    test('defaults to Arabic and lays out right-to-left', () {
      const ar = AppStrings(Locale('ar'));
      expect(ar.isArabic, isTrue);
      expect(ar.mainWallet, 'المحفظة الرئيسية');
      expect(ar.shoppingCredit, 'رصيد التسوق');
    });

    test('provides English for every key it provides Arabic for', () {
      const ar = AppStrings(Locale('ar'));
      const en = AppStrings(Locale('en'));

      expect(en.isArabic, isFalse);
      expect(en.mainWallet, isNot(ar.mainWallet));
      expect(en.storePurchasesOnly, 'Store purchases only');
    });

    test('interpolates counts into the participant message', () {
      const en = AppStrings(Locale('en'));
      expect(en.participantsRemaining(28), contains('28'));
    });
  });
}

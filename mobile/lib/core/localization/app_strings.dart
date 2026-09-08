import 'package:flutter/widgets.dart';

/// User-facing strings for both supported languages.
///
/// Arabic is the default and lays out right-to-left. Nothing here leaks into business logic:
/// screens read strings through [AppStrings.of], and the backend sends localization keys rather
/// than rendered text.
class AppStrings {
  const AppStrings(this.locale);

  final Locale locale;

  static AppStrings of(BuildContext context) =>
      Localizations.of<AppStrings>(context, AppStrings) ?? const AppStrings(Locale('ar'));

  static const supportedLocales = [Locale('ar'), Locale('en')];

  bool get isArabic => locale.languageCode == 'ar';

  String _t(String ar, String en) => isArabic ? ar : en;

  // ---- general
  String get appName => _t('منصة المزادات', 'Auction Platform');
  String get retry => _t('إعادة المحاولة', 'Retry');
  String get cancel => _t('إلغاء', 'Cancel');
  String get confirm => _t('تأكيد', 'Confirm');
  String get close => _t('إغلاق', 'Close');
  String get loading => _t('جارٍ التحميل…', 'Loading…');
  String get somethingWentWrong => _t('حدث خطأ ما', 'Something went wrong');
  String get nothingHere => _t('لا يوجد شيء هنا بعد', 'Nothing here yet');
  String get save => _t('حفظ', 'Save');

  // ---- auth
  String get login => _t('تسجيل الدخول', 'Sign in');
  String get register => _t('إنشاء حساب', 'Create account');
  String get logout => _t('تسجيل الخروج', 'Sign out');
  String get email => _t('البريد الإلكتروني', 'Email');
  String get password => _t('كلمة المرور', 'Password');
  String get fullName => _t('الاسم الكامل', 'Full name');
  String get phone => _t('رقم الهاتف', 'Phone');
  String get noAccount => _t('ليس لديك حساب؟', 'No account yet?');
  String get haveAccount => _t('لديك حساب بالفعل؟', 'Already have an account?');

  // ---- one-time codes
  String get signInWithCode => _t('الدخول برمز التحقق', 'Sign in with a code');
  String get signInWithPassword => _t('الدخول بكلمة المرور', 'Sign in with a password');
  String get emailOrPhone => _t('البريد الإلكتروني أو رقم الهاتف', 'Email or phone number');
  String get sendCode => _t('إرسال الرمز', 'Send code');
  String get codeSentTo => _t('تم إرسال الرمز إلى', 'We sent a code to');
  String get enterCode => _t('أدخل رمز التحقق', 'Enter the code');
  String get resendCode => _t('إعادة إرسال الرمز', 'Resend code');
  String resendIn(int seconds) =>
      _t('إعادة الإرسال بعد $seconds ثانية', 'Resend in ${seconds}s');
  String codeExpiresIn(int seconds) =>
      _t('ينتهي الرمز خلال $seconds ثانية', 'Code expires in ${seconds}s');
  String get codeExpired => _t('انتهت صلاحية الرمز', 'The code has expired');
  String get changeDestination => _t('تغيير', 'Change');

  String get confirmWithdrawal => _t('تأكيد السحب', 'Confirm withdrawal');
  String get withdrawalNeedsCode => _t(
        'لحمايتك، نرسل رمز تأكيد قبل تنفيذ السحب.',
        'For your protection, we send a confirmation code before a withdrawal goes through.',
      );
  String withdrawalSummary(String amount, String destination) => _t(
        'سحب $amount إلى $destination',
        'Withdraw $amount to $destination',
      );
  String get withdrawalDestination => _t('حساب الاستلام', 'Destination account');
  String get withdrawalSent => _t('تم تنفيذ طلب السحب', 'Withdrawal submitted');
  String get offlineSessionKept => _t(
        'لا يوجد اتصال. جلستك محفوظة.',
        'No connection. Your session is saved.',
      );

  // ---- navigation
  String get home => _t('الرئيسية', 'Home');
  String get auctions => _t('المزادات', 'Auctions');
  String get store => _t('المتجر', 'Store');
  String get wallet => _t('المحفظة', 'Wallet');
  String get profile => _t('حسابي', 'Profile');

  // ---- auctions
  String get liveAuctions => _t('مزادات مباشرة', 'Live now');
  String get upcomingAuctions => _t('مزادات قادمة', 'Upcoming');
  String get completedAuctions => _t('مزادات منتهية', 'Completed');
  String get originalPrice => _t('السعر الأصلي', 'Original price');
  String get currentPrice => _t('السعر الحالي', 'Current price');
  String get targetPrice => _t('السعر المستهدف', 'Target price');
  String get startingPrice => _t('سعر البداية', 'Starting price');
  String get entryFee => _t('رسوم الاشتراك', 'Entry fee');
  String get participants => _t('المشاركون', 'Participants');
  String get prizes => _t('الجوائز', 'Prizes');
  String get joinAuction => _t('اشترك في المزاد', 'Join auction');
  String get joined => _t('أنت مشترك', 'You have joined');
  String get yourRank => _t('ترتيبك', 'Your rank');
  String get yourHighest => _t('أعلى سعر لك', 'Your highest price');
  String get leaderboard => _t('المتصدرون', 'Leaderboard');
  String get winners => _t('الفائزون', 'Winners');
  String get finalPhase => _t('المرحلة النهائية', 'Final phase');
  String get auctionStartsIn => _t('يبدأ المزاد خلال', 'Starts in');
  String get remaining => _t('الوقت المتبقي', 'Time left');
  String get progressToTarget => _t('التقدم نحو الهدف', 'Progress to target');

  String participantsRemaining(int n) =>
      _t('باقي $n مشارك', '$n participants remaining');

  String countdownComplete(int count, int required) =>
      _t('$count / $required ✓ العدد اكتمل', '$count / $required ✓ target reached');

  String get willNotifyOnStart =>
      _t('سنرسل لك إشعاراً عند بدء المزاد.', 'We will notify you when the auction starts.');

  String bidButton(String amount) => _t('زايد +$amount', 'BID +$amount');

  String get bidIncrementNote => _t(
        'هذا المبلغ يرفع سعر المزاد ولا يُخصم منك.',
        'This raises the auction price. It is not charged to you.',
      );

  String get inTopFour => _t('🎉 أنت الآن ضمن الفائزين!', '🎉 You are in the winning four!');
  String get lostFourthPlace => _t('⚠️ فقدت المركز الرابع!', '⚠️ You dropped out of the top four!');

  String get connectionLost =>
      _t('انقطع الاتصال. جارٍ إعادة المزامنة…', 'Connection lost. Resyncing…');
  String get reconnected => _t('تمت إعادة الاتصال', 'Reconnected');
  String get bidsNeedConnection => _t(
        'المزايدة تتطلب اتصالاً مباشراً بالخادم.',
        'Bidding requires a live connection to the server.',
      );

  // ---- results
  String get auctionFinished => _t('انتهى المزاد', 'Auction finished');
  String get congratulations => _t('🎉 مبروك!', '🎉 Congratulations!');
  String youFinished(int rank) => _t('حصلت على المركز $rank', 'You finished #$rank');
  String get didNotPlace =>
      _t('لم تكن ضمن الأربعة الأوائل.', 'You did not finish in the top 4.');
  String entryFeeReturned(String amount) => _t(
        'تمت إعادة رسوم الاشتراك $amount كرصيد تسوق.',
        'Your $amount entry fee was returned as shopping credit.',
      );
  String get shopNow => _t('تسوق الآن', 'Shop now');
  String get payForPrize => _t('ادفع قيمة الجائزة', 'Pay for prize');
  String get paymentRequired => _t('الدفع مطلوب', 'Payment required');
  String get prizeValue => _t('قيمة الجائزة', 'Prize value');
  String get myWins => _t('جوائزي', 'My wins');

  // ---- wallets
  String get mainWallet => _t('المحفظة الرئيسية', 'Main Wallet');
  String get shoppingCredit => _t('رصيد التسوق', 'Shopping Credit');
  String get withdrawable => _t('قابل للسحب', 'Withdrawable');
  String get storePurchasesOnly => _t('للشراء من المتجر فقط', 'Store purchases only');
  String get deposit => _t('إيداع', 'Deposit');
  String get withdraw => _t('سحب', 'Withdraw');
  String get transactions => _t('الحركات', 'Transactions');
  String get mainWalletHistory => _t('حركات المحفظة الرئيسية', 'Main wallet history');
  String get shoppingWalletHistory => _t('حركات رصيد التسوق', 'Shopping credit history');
  String get amount => _t('المبلغ', 'Amount');

  String get shoppingCreditCannotBeWithdrawn => _t(
        'رصيد التسوق لا يمكن سحبه أو تحويله. يُستخدم في المتجر فقط.',
        'Shopping credit cannot be withdrawn or transferred. It is for store purchases only.',
      );

  // ---- store
  String get featured => _t('منتجات مميزة', 'Featured');
  String get categories => _t('الأقسام', 'Categories');
  String get newArrivals => _t('وصل حديثاً', 'New arrivals');
  String get popular => _t('الأكثر مبيعاً', 'Popular');
  String get buyWithShoppingCredit => _t('يقبل رصيد التسوق', 'Shopping credit accepted');
  String get addToCart => _t('أضف إلى السلة', 'Add to cart');
  String get cart => _t('السلة', 'Cart');
  String get checkout => _t('إتمام الشراء', 'Checkout');
  String get subtotal => _t('المجموع الفرعي', 'Subtotal');
  String get shipping => _t('الشحن', 'Shipping');
  String get discount => _t('الخصم', 'Discount');
  String get tax => _t('الضريبة', 'Tax');
  String get total => _t('الإجمالي', 'Total');
  String get available => _t('المتاح', 'Available');
  String get required => _t('المطلوب', 'Required');
  String get outOfStock => _t('نفدت الكمية', 'Out of stock');
  String get inStock => _t('متوفر', 'In stock');
  String get myOrders => _t('طلباتي', 'My orders');
  String get orderPlaced => _t('تم إنشاء الطلب', 'Order placed');
  String get emptyCart => _t('سلتك فارغة', 'Your cart is empty');
  String get payWith => _t('طريقة الدفع', 'Pay with');
  String get quantity => _t('الكمية', 'Quantity');
  String get cancelOrder => _t('إلغاء الطلب', 'Cancel order');

  // ---- notifications & profile
  String get notifications => _t('الإشعارات', 'Notifications');
  String get markAllRead => _t('تعليم الكل كمقروء', 'Mark all read');
  String get language => _t('اللغة', 'Language');
  String get arabic => _t('العربية', 'Arabic');
  String get english => _t('الإنجليزية', 'English');
  String get auctionHistory => _t('سجل المزادات', 'Auction history');
  String get addresses => _t('العناوين', 'Addresses');
}

class AppStringsDelegate extends LocalizationsDelegate<AppStrings> {
  const AppStringsDelegate();

  @override
  bool isSupported(Locale locale) => ['ar', 'en'].contains(locale.languageCode);

  @override
  Future<AppStrings> load(Locale locale) async => AppStrings(locale);

  @override
  bool shouldReload(AppStringsDelegate old) => false;
}

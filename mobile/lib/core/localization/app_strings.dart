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
  // A brand name is not translated, so both languages get the same word.
  String get appName => 'DealGo';
  String get tagline => _t('صفقتك جاهزة', 'Your deal is ready');
  String get retry => _t('إعادة المحاولة', 'Retry');
  String get cancel => _t('إلغاء', 'Cancel');
  String get confirm => _t('تأكيد', 'Confirm');
  String get close => _t('إغلاق', 'Close');
  String get loading => _t('جارٍ التحميل…', 'Loading…');
  String get somethingWentWrong => _t('حدث خطأ ما', 'Something went wrong');
  String get nothingHere => _t('لا يوجد شيء هنا بعد', 'Nothing here yet');
  String get save => _t('حفظ', 'Save');
  String get seeAll => _t('عرض الكل', 'See all');
  String get canAlsoPayHere => _t('يمكن الدفع به هنا أيضاً', 'Can also pay here');
  String get acceptsBothWallets => _t('المحفظتان مقبولتان', 'Both wallets accepted');

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
  String get phoneSignInHint => _t(
        'أدخل رقم جوالك السعودي وسنرسل لك رمز تحقق. إذا كان الرقم جديداً سننشئ لك حساباً.',
        "Enter your Saudi mobile number and we'll text you a code. A new number gets a new account.",
      );
  String get mobileNumber => _t('رقم الجوال', 'Mobile number');
  String get invalidSaudiPhone => _t(
        'أدخل رقم جوال سعودي صحيح يبدأ بـ 5، مثل 05XXXXXXXX.',
        'Enter a valid Saudi mobile number starting with 5, e.g. 05XXXXXXXX.',
      );
  String get whatsYourName => _t('ما اسمك؟', "What's your name?");
  String get nameHelp => _t(
        'يظهر اسمك في المزادات والطلبات.',
        'Your name appears on your auctions and orders.',
      );
  String get continueLabel => _t('متابعة', 'Continue');

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

  // ---- home page
  String get searchProducts => _t('ابحث عن منتج', 'Search products');
  String get allCategories => _t('الكل', 'All');
  String get saveUpTo => _t('وفّر حتى', 'Save up to');
  String get enterAuction => _t('ادخل المزاد', 'Enter auction');
  String get dealGoStore => _t('متجر DealGo', 'DealGo store');
  String get shopWithCredit => _t('تسوّق برصيدك', 'Shop with your credit');
  String get pickedForYou => _t('مختارات لك', 'Picked for you');
  String get offers => _t('العروض', 'Offers');
  String offersIn(String category) => _t('عروض $category', '$category offers');
  String get priceBeforeOffer => _t('قبل الخصم', 'Before the offer');
  String discountOff(int percent) => _t('خصم $percent%', '$percent% off');
  String get myPrizes => _t('جوائزي', 'My prizes');
  String get couldNotOpenLink => _t('تعذّر فتح الرابط.', 'Could not open the link.');

  // ---- the featured auction on the front page
  String get featuredAuctions => _t('المزادات المميزة', 'Featured auctions');
  String get seeAllAuctions => _t('عرض جميع المزادات', 'See all auctions');
  String get shopByCategory => _t('تسوق حسب القسم', 'Shop by category');
  String get seeAllCategories => _t('عرض جميع الأقسام', 'See all categories');
  String get inAuctionNow => _t('في المزاد الآن', 'In the auction now');
  String get startsIn => _t('يبدأ خلال', 'Starts in');
  String get hoursShort => _t('ساعة', 'hours');
  String get minutesShort => _t('دقيقة', 'min');
  String get secondsShort => _t('ثانية', 'sec');
  String get storeWorldTitle => _t('عالم من الصفقات بانتظارك', 'A world of deals is waiting');
  String get storeWorldBody => _t('اكتشف المزيد في متجرنا', 'Discover more in our store');
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
  String get yourNextBid => _t('مزايدتك القادمة', 'Your next bid');
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
  String get topBidders => _t('المتصدرون', 'Top bidders');
  String get topBiddersSubtitle =>
      _t('أكثر المشاركين تقدّمًا في المزايدة الآن', 'The participants furthest ahead right now');
  String get noBidsYet => _t('لم يزايد أحد بعد. كن أول المزايدين!', 'No bids yet. Be the first!');
  String get winningZoneEnds => _t('حدود المراكز الفائزة', 'Prize places end here');
  String bidsCount(int count) => isArabic
      ? (count == 1 ? 'مزايدة واحدة' : count == 2 ? 'مزايدتان' : count <= 10 ? '$count مزايدات' : '$count مزايدة')
      : (count == 1 ? '1 bid' : '$count bids');
  String showAllBidders(int count) =>
      _t('عرض جميع المزايدين ($count)', 'Show all bidders ($count)');
  String get you => _t('أنت', 'You');
  String get noBidYet => _t('لم يزايد بعد', 'No bid yet');
  String participantsCount(int count) =>
      isArabic ? '$count مشارك' : '$count bidders';
  String get winners => _t('الفائزون', 'Winners');
  String get finalPhase => _t('المرحلة النهائية', 'Final phase');
  String get auctionStartsIn => _t('يبدأ المزاد خلال', 'Starts in');

  // ---- the upcoming auctions on the front page
  String get almostFull => _t('كاد يكتمل', 'Almost full');
  String get lastSeats => _t('آخر المقاعد', 'Last seats');
  String get reserveYourSeat => _t('احجز مقعدك', 'Reserve your seat');
  String get remaining => _t('الوقت المتبقي', 'Time left');
  String get progressToTarget => _t('التقدم نحو الهدف', 'Progress to target');

  String participantsRemaining(int n) =>
      _t('باقي $n مشارك', '$n participants remaining');

  String countdownComplete(int count, int required) =>
      _t('$count / $required ✓ العدد اكتمل', '$count / $required ✓ target reached');

  // Participant numbers are not shown in the app, only how full the auction is.
  String get participantsFilling => _t('اكتمال عدد المشاركين', 'Participants joined');
  String get participantsComplete => _t('✓ اكتمل عدد المشاركين', '✓ All places are filled');

  // Prize pricing: every prize gets the auction's discount, not only the first.
  String devicePrice(String amount) => _t('سعر الجهاز $amount', 'Device price $amount');
  String youGetItFor(String amount) => _t('وستحصل عليه بسعر $amount', 'You get it for $amount');
  String youGetItForAtMost(String amount) => _t(
        'وستحصل عليه بسعر لا يزيد عن $amount',
        'You get it for at most $amount',
      );
  String afterDiscount(String percent) => _t('بعد خصم $percent', 'after a $percent discount');
  String get discountAppliesToAll => _t(
        'نسبة الخصم تُطبّق على جميع الجوائز.',
        'The discount applies to every prize.',
      );
  String prizeRank(int rank) => _t('المركز $rank', 'Place $rank');

  // ---- live bidding (the focused screen while an auction runs)
  String get liveBidding => _t('المزايدة المباشرة', 'Live bidding');
  String get arenaLive => _t('مباشر الآن', 'Live now');
  String get enterArena => _t('متابعة المزايدة المباشرة', 'Open live bidding');
  String get arenaConnecting => _t('جارٍ الاتصال بالمزاد…', 'Connecting to the auction…');
  String get arenaLeave => _t('العودة إلى تفاصيل المزاد', 'Back to the auction');
  String get finalPhaseStarted =>
      _t('بدأت المرحلة النهائية، والوقت المتبقي محدود.', 'The final phase has started; time is short.');
  String get outbidNotice => _t('قدّم مشارك آخر مزايدة أعلى من سعرك.', 'Another participant bid above your price.');
  String get auctionEndedBody =>
      _t('اكتمل المزاد واحتُسب الترتيب النهائي لجميع المشاركين.', "The auction is over and every participant's final place is set.");
  String get viewResults => _t('عرض النتائج', 'View results');
  String get waitForOtherBidder => _t('أنت صاحب آخر مزايدة — انتظر حتى يزايد غيرك',
      'You placed the last bid — wait for someone else to bid');

  // ---- results
  String get auctionResults => _t('نتائج المزاد', 'Auction results');
  String get closingPrice => _t('سعر الإغلاق', 'Closing price');
  String get totalBids => _t('عدد المزايدات', 'Total bids');
  String get finalStandings => _t('الترتيب النهائي', 'Final standings');
  String get finalStandingsSubtitle =>
      _t('ترتيب جميع المزايدين عند انتهاء المزاد', "Every bidder's place when the auction closed");
  String get noBidsInAuction => _t('لم تُقدَّم أي مزايدة في هذا المزاد.', 'No bids were placed in this auction.');
  String participantsWithoutBids(int count) => _t(
      count == 1 ? 'ومشارك واحد لم يزايد' : 'و$count مشاركين لم يزايدوا',
      count == 1 ? 'and 1 participant who did not bid' : 'and $count participants who did not bid');
  String get yourFinalPlace => _t('مركزك النهائي', 'Your final place');
  String get noBidPlaced => _t('لم تزايد في هذا المزاد', 'You did not bid');
  String placeOutOf(int rank, int count) =>
      _t('المركز $rank من $count', 'Place $rank of $count');
  String youWonPrize(String prize) => _t('فزت بـ $prize', 'You won $prize');
  String prizeAmountDue(String amount) =>
      _t('المبلغ المستحق لاستلام الجائزة: $amount', 'Amount due to claim the prize: $amount');
  String get viewFullResults => _t('عرض النتائج الكاملة', 'View full results');

  /// A prize claim's state, from the server's status name.
  String claimStatus(String status) => switch (status) {
        'Pending' || 'PaymentRequired' => _t('بانتظار الدفع', 'Payment due'),
        'PaymentProcessing' => _t('جارٍ الدفع', 'Payment in progress'),
        'Paid' => _t('تم الدفع', 'Paid'),
        'Claimed' => _t('تم الاستلام', 'Claimed'),
        'Fulfilled' => _t('تم التسليم', 'Delivered'),
        'Cancelled' => _t('ملغاة', 'Cancelled'),
        'Expired' => _t('انتهت المهلة', 'Expired'),
        _ => status,
      };

  String get willNotifyOnStart =>
      _t('سنرسل لك إشعاراً عند بدء المزاد.', 'We will notify you when the auction starts.');

  String bidButton(String amount) => _t('زايد +$amount', 'BID +$amount');

  String get bidIncrementNote => _t(
        'هذا المبلغ يرفع سعر المزاد ولا يُخصم منك.',
        'This raises the auction price. It is not charged to you.',
      );

  String get inTopFour => _t('أصبحت ضمن المراكز الأربعة الفائزة.', 'You are now in the four prize places.');
  String get lostFourthPlace => _t('لم تعد ضمن المراكز الأربعة الفائزة.', 'You are no longer in the four prize places.');

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
  String get productDetails => _t('تفاصيل المنتج', 'Product details');
  String get aboutProduct => _t('عن المنتج', 'About this product');
  String youSave(String amount) => _t('وفّر $amount', 'Save $amount');
  String get cart => _t('السلة', 'Cart');
  String get featuredBadge => _t('مميز', 'Featured');
  String get similarProducts => _t('منتجات مشابهة', 'You may also like');
  String get readMore => _t('اقرأ المزيد', 'Read more');
  String get showLess => _t('عرض أقل', 'Show less');
  String get addedToCart => _t('تمت الإضافة إلى السلة', 'Added to cart');
  String get viewCart => _t('عرض السلة', 'View cart');
  String get genuineProduct => _t('منتج أصلي', 'Genuine');
  String get securePayment => _t('دفع آمن', 'Secure payment');
  String get fastDelivery => _t('توصيل سريع', 'Fast delivery');
  String get fastShipping => _t('شحن سريع', 'Fast shipping');
  String get freeShipping => _t('شحن مجاني', 'Free shipping');
  String onlyLeft(int n) => _t('بقي $n فقط', 'Only $n left');
  String get menu => _t('القائمة', 'Menu');

  // The app's own banners, for the strip under the search box.
  String houseAdSaveUpTo(int percent) =>
      isArabic ? 'وفّر حتى $percent% في المتجر' : 'Save up to $percent% in the store';
  String get houseAdStoreHeadline => _t('عروض المتجر بين يديك', "The store's offers, right here");
  String get houseAdStoreBody => _t('أسعار مخفّضة على منتجات مختارة', 'Selected items, reduced');
  String get houseAdStoreAction => _t('تسوّق العروض', 'Shop offers');
  String departmentSaveUpTo(int percent) =>
      isArabic ? 'وفّر حتى $percent% في هذا القسم' : 'Save up to $percent% here';
  String departmentAdBody(int count) =>
      isArabic ? 'أكثر من $count منتج في هذا القسم' : '$count products in this department';
  String get houseAdDiscountsHeadline =>
      _t('خصومات تجري الآن في المتجر', 'Discounts running in the store');

  String get houseAdAuctionHeadline =>
      _t('زايد اليوم واربح جهازك بأقل سعر', 'Bid today and win for less');
  String get houseAdAuctionBody =>
      _t('مزادات مباشرة وجوائز فورية', 'Live auctions, prizes on the spot');
  String get houseAdAuctionAction => _t('شارك الآن', 'Join now');
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
  String get privacyPolicy => _t('سياسة الخصوصية', 'Privacy policy');
  String get termsOfUse => _t('الشروط والأحكام', 'Terms of use');
  String get agreeToTermsPrefix =>
      _t('بالمتابعة فإنك توافق على', 'By continuing you agree to the');
  String get and => _t('و', 'and ');
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

  // ---- server errors
  //
  // The API answers with a stable `code` and an English `message`. Screens show the text
  // below instead of that message, so the user always reads Arabic. The English message
  // is still the fallback for a code added on the server but not yet listed here — better
  // an English sentence than a blank screen.
  String errorFor(String code, String fallback) =>
      isArabic ? (arabicError(code) ?? fallback) : fallback;

  /// Static so controllers and other code without a [BuildContext] can reach it.
  static String? arabicError(String code) => _errorsAr[code];

  /// For a failure that has no code of its own. The server's English wording is never shown.
  static const genericError = 'حدث خطأ غير متوقع. حاول مرة أخرى.';
  static const genericBidError = 'تعذّرت المزايدة. حاول مرة أخرى.';

  static const _errorsAr = <String, String>{
    'AUCTION_NOT_FOUND': 'المزاد غير موجود.',
    'AUCTION_NOT_ACTIVE': 'المزاد غير نشط الآن.',
    'AUCTION_FINISHED': 'انتهى المزاد.',
    'AUCTION_NOT_READY': 'لم يكتمل عدد المشاركين بعد.',
    'INVALID_AUCTION_STATE': 'حالة المزاد تغيّرت. حدّث الصفحة وحاول مرة أخرى.',
    'INVALID_ORDER_STATE': 'حالة الطلب تغيّرت. حدّث الصفحة وحاول مرة أخرى.',
    'REGISTRATION_CLOSED': 'انتهى التسجيل في هذا المزاد.',
    'AUCTION_FULL': 'اكتمل عدد المشاركين.',
    'PARTICIPANT_NOT_FOUND': 'أنت غير مشترك في هذا المزاد.',
    'ALREADY_JOINED': 'أنت مشترك في هذا المزاد بالفعل.',
    'ENTRY_PAYMENT_REQUIRED': 'يجب دفع رسوم الاشتراك أولاً.',
    'BID_NOT_ALLOWED': 'لا يمكن المزايدة الآن.',
    'CONSECUTIVE_BID_NOT_ALLOWED': 'أنت صاحب آخر مزايدة. انتظر حتى يزايد غيرك.',
    'TARGET_ALREADY_REACHED': 'تم الوصول إلى السعر المستهدف.',
    'DUPLICATE_REQUEST': 'تم تنفيذ هذا الطلب من قبل.',
    'RATE_LIMITED': 'محاولات كثيرة. انتظر قليلاً ثم أعد المحاولة.',
    'INSUFFICIENT_BALANCE': 'رصيد المحفظة الرئيسية غير كافٍ.',
    'INSUFFICIENT_SHOPPING_CREDIT': 'رصيد التسوق غير كافٍ.',
    'SHOPPING_WALLET_OPERATION_NOT_ALLOWED':
        'رصيد التسوق للشراء من المتجر فقط، ولا يمكن سحبه أو تحويله.',
    'OUT_OF_STOCK': 'الكمية غير متوفرة.',
    'PRODUCT_NOT_AVAILABLE': 'المنتج غير متاح.',
    'ORDER_NOT_CANCELLABLE': 'لا يمكن إلغاء هذا الطلب.',
    'INVALID_CREDENTIALS': 'البريد الإلكتروني أو كلمة المرور غير صحيحة.',
    'INVALID_OTP': 'الرمز غير صحيح.',
    'OTP_EXPIRED': 'انتهت صلاحية الرمز. اطلب رمزاً جديداً.',
    'OTP_REQUIRED': 'أدخل رمز التحقق للمتابعة.',
    'OTP_ALREADY_USED': 'تم استخدام هذا الرمز من قبل.',
    'OTP_TOO_MANY_ATTEMPTS': 'محاولات خاطئة كثيرة. اطلب رمزاً جديداً.',
    'OTP_REQUEST_TOO_SOON': 'انتظر قليلاً قبل طلب رمز جديد.',
    'OTP_DESTINATION_INVALID': 'البريد الإلكتروني أو رقم الهاتف غير صحيح.',
    'PHONE_NUMBER_INVALID': 'أدخل رقم جوال سعودي صحيح يبدأ بـ 5، مثل 05XXXXXXXX.',
    'PHONE_NUMBER_TAKEN': 'رقم الجوال هذا مرتبط بحساب آخر.',
    'EMAIL_ALREADY_REGISTERED': 'هذا البريد الإلكتروني مسجل بالفعل.',
    'ACCOUNT_DISABLED': 'تم إيقاف هذا الحساب.',
    'UNAUTHORIZED': 'انتهت الجلسة. سجّل الدخول من جديد.',
    'FORBIDDEN': 'ليس لديك صلاحية لهذا الإجراء.',
    'VALIDATION_FAILED': 'تحقق من البيانات المدخلة.',
    'INTERNAL_ERROR': 'حدث خطأ في الخادم. حاول مرة أخرى.',
    'NETWORK_ERROR': 'لا يوجد اتصال بالخادم.',
    // The platform is up but something it needs is not answering: the database, or the provider
    // that sends the verification codes. Nothing the user did, and nothing they can fix.
    'SERVICE_UNAVAILABLE': 'الخدمة غير متاحة مؤقتاً. حاول بعد لحظات.',
    'NOT_FOUND': 'العنصر غير موجود.',
    'CONFLICT': 'تم تعديل البيانات من طلب آخر. أعد المحاولة.',
    'INVALID_REFRESH_TOKEN': 'انتهت الجلسة. سجّل الدخول من جديد.',
    'INVALID_AMOUNT': 'أدخل مبلغاً أكبر من صفر.',
    'WALLET_NOT_FOUND': 'المحفظة غير موجودة.',
    'CURRENCY_MISMATCH': 'عملة المحفظة لا تطابق عملة العملية.',
    'PRODUCT_NOT_FOUND': 'المنتج غير موجود.',
    'ORDER_NOT_FOUND': 'الطلب غير موجود.',
    'ORDER_AMOUNT_OUT_OF_RANGE': 'قيمة الطلب خارج الحد المسموح.',
    'PAYMENT_METHOD_NOT_ACCEPTED': 'طريقة الدفع هذه غير مقبولة لهذا المنتج.',
    'STORE_DISABLED': 'المتجر غير متاح حالياً.',
    'CATEGORY_NOT_FOUND': 'القسم غير موجود.',
    'PRIZE_CLAIM_NOT_FOUND': 'المطالبة غير موجودة.',
    'PRIZE_CLAIM_NOT_PAYABLE': 'لا يمكن دفع هذه المطالبة الآن.',
    'PRIZE_CLAIM_NOT_CLAIMABLE': 'لا يمكن استلام هذه الجائزة الآن.',
  };
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

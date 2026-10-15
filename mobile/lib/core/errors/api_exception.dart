import 'package:mobile/core/localization/app_strings.dart';

/// The error envelope every endpoint returns: `{ code, message, details }`.
///
/// The UI branches on [code], never on the message text, so wording can change without
/// breaking behaviour.
class ApiException implements Exception {
  const ApiException({
    required this.code,
    required this.message,
    this.details = const [],
    this.statusCode,
  });

  final String code;
  final String message;
  final List<String> details;
  final int? statusCode;

  factory ApiException.fromResponse(Map<String, dynamic> body, int? statusCode) {
    return ApiException(
      code: body['code'] as String? ?? ErrorCodes.internalError,
      message: body['message'] as String? ?? 'Something went wrong.',
      details: (body['details'] as List?)?.map((e) => e.toString()).toList() ?? const [],
      statusCode: statusCode,
    );
  }

  factory ApiException.network() => const ApiException(
        code: ErrorCodes.networkError,
        message: 'No connection to the server.',
      );

  /// What the user should read. [message] is the server's English text, kept for logs and
  /// for any code that has no Arabic wording yet.
  String get displayMessage => AppStrings.arabicError(code) ?? message;

  @override
  String toString() => '$code: $message';
}

/// Mirrors the backend's ErrorCodes so both sides agree on the contract.
class ErrorCodes {
  const ErrorCodes._();

  static const auctionNotFound = 'AUCTION_NOT_FOUND';
  static const auctionNotActive = 'AUCTION_NOT_ACTIVE';
  static const auctionFinished = 'AUCTION_FINISHED';
  static const auctionNotReady = 'AUCTION_NOT_READY';
  static const registrationClosed = 'REGISTRATION_CLOSED';
  static const auctionFull = 'AUCTION_FULL';

  static const participantNotFound = 'PARTICIPANT_NOT_FOUND';
  static const alreadyJoined = 'ALREADY_JOINED';
  static const entryPaymentRequired = 'ENTRY_PAYMENT_REQUIRED';

  static const bidNotAllowed = 'BID_NOT_ALLOWED';
  static const consecutiveBidNotAllowed = 'CONSECUTIVE_BID_NOT_ALLOWED';
  static const targetAlreadyReached = 'TARGET_ALREADY_REACHED';
  static const duplicateRequest = 'DUPLICATE_REQUEST';
  static const rateLimited = 'RATE_LIMITED';

  static const insufficientBalance = 'INSUFFICIENT_BALANCE';
  static const insufficientShoppingCredit = 'INSUFFICIENT_SHOPPING_CREDIT';
  static const shoppingWalletOperationNotAllowed = 'SHOPPING_WALLET_OPERATION_NOT_ALLOWED';

  static const outOfStock = 'OUT_OF_STOCK';
  static const productNotAvailable = 'PRODUCT_NOT_AVAILABLE';
  static const orderNotCancellable = 'ORDER_NOT_CANCELLABLE';

  static const invalidCredentials = 'INVALID_CREDENTIALS';

  // One-time codes, for the sign-in and withdrawal flows.
  static const invalidOtp = 'INVALID_OTP';
  static const otpExpired = 'OTP_EXPIRED';
  static const otpRequired = 'OTP_REQUIRED';
  static const otpAlreadyUsed = 'OTP_ALREADY_USED';
  static const otpTooManyAttempts = 'OTP_TOO_MANY_ATTEMPTS';
  static const otpRequestTooSoon = 'OTP_REQUEST_TOO_SOON';
  static const otpDestinationInvalid = 'OTP_DESTINATION_INVALID';
  static const emailAlreadyRegistered = 'EMAIL_ALREADY_REGISTERED';
  static const accountDisabled = 'ACCOUNT_DISABLED';
  static const unauthorized = 'UNAUTHORIZED';
  static const forbidden = 'FORBIDDEN';

  static const validationFailed = 'VALIDATION_FAILED';
  static const internalError = 'INTERNAL_ERROR';
  static const networkError = 'NETWORK_ERROR';
}

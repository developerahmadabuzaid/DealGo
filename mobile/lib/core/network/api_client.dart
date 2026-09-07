import 'dart:async';

import 'package:dio/dio.dart';

import '../config/app_config.dart';
import '../errors/api_exception.dart';
import '../storage/token_store.dart';

/// The single HTTP entry point.
///
/// Attaches the bearer token, refreshes it once on a 401, and normalises every failure into
/// [ApiException] so screens never deal with raw Dio errors.
class ApiClient {
  ApiClient({required TokenStore tokenStore, Dio? dio})
      // ignore: prefer_initializing_formals -- keeps the parameter name public for callers.
      : _tokenStore = tokenStore,
        _dio = dio ??
            Dio(BaseOptions(
              baseUrl: AppConfig.apiRoot,
              connectTimeout: AppConfig.requestTimeout,
              receiveTimeout: AppConfig.requestTimeout,
              headers: {'Content-Type': 'application/json'},
              // Non-2xx is handled here rather than thrown by Dio, so the error envelope
              // survives intact.
              validateStatus: (_) => true,
            )) {
    _dio.interceptors.add(InterceptorsWrapper(onRequest: _onRequest));
  }

  final Dio _dio;
  final TokenStore _tokenStore;

  /// Invoked when the session cannot be recovered, so the app can return to the login screen.
  void Function()? onSessionExpired;

  Future<void> _onRequest(RequestOptions options, RequestInterceptorHandler handler) async {
    final tokens = await _tokenStore.read();
    if (tokens != null) {
      options.headers['Authorization'] = 'Bearer ${tokens.accessToken}';
    }
    handler.next(options);
  }

  Future<String?> currentAccessToken() async => (await _tokenStore.read())?.accessToken;

  Future<dynamic> get(String path, {Map<String, dynamic>? query}) =>
      _send(() => _dio.get(path, queryParameters: _clean(query)));

  Future<dynamic> post(String path, {Object? body, Map<String, dynamic>? query}) =>
      _send(() => _dio.post(path, data: body, queryParameters: _clean(query)));

  Future<dynamic> put(String path, {Object? body}) =>
      _send(() => _dio.put(path, data: body));

  Future<dynamic> patch(String path, {Object? body}) =>
      _send(() => _dio.patch(path, data: body));

  Future<dynamic> delete(String path, {Map<String, dynamic>? query}) =>
      _send(() => _dio.delete(path, queryParameters: _clean(query)));

  /// Dio drops nulls poorly in query strings; stripping them keeps URLs clean.
  Map<String, dynamic>? _clean(Map<String, dynamic>? query) {
    if (query == null) return null;
    final cleaned = <String, dynamic>{};
    query.forEach((k, v) {
      if (v != null) cleaned[k] = v;
    });
    return cleaned.isEmpty ? null : cleaned;
  }

  Future<dynamic> _send(Future<Response> Function() request, {bool allowRefresh = true}) async {
    late Response response;

    try {
      response = await request();
    } on DioException {
      throw ApiException.network();
    }

    if (response.statusCode == 401 && allowRefresh) {
      if (await _refresh()) {
        return _send(request, allowRefresh: false);
      }
      onSessionExpired?.call();
      throw const ApiException(
        code: ErrorCodes.unauthorized,
        message: 'Your session has expired. Please sign in again.',
        statusCode: 401,
      );
    }

    final status = response.statusCode ?? 0;
    if (status >= 200 && status < 300) return response.data;

    final data = response.data;
    if (data is Map<String, dynamic>) {
      throw ApiException.fromResponse(data, status);
    }

    throw ApiException(
      code: ErrorCodes.internalError,
      message: 'Request failed ($status).',
      statusCode: status,
    );
  }

  Completer<bool>? _refreshInFlight;

  /// Rotates the refresh token. Concurrent 401s share one refresh so a burst of requests
  /// cannot consume the single-use token more than once.
  Future<bool> _refresh() async {
    if (_refreshInFlight != null) return _refreshInFlight!.future;

    final completer = Completer<bool>();
    _refreshInFlight = completer;

    try {
      final tokens = await _tokenStore.read();
      if (tokens == null) {
        completer.complete(false);
        return false;
      }

      final response = await Dio(BaseOptions(
        baseUrl: AppConfig.apiRoot,
        validateStatus: (_) => true,
      )).post('/auth/refresh', data: {'refreshToken': tokens.refreshToken});

      if (response.statusCode != 200 || response.data is! Map) {
        await _tokenStore.clear();
        completer.complete(false);
        return false;
      }

      final body = response.data as Map<String, dynamic>;
      await _tokenStore.write(AuthTokens(
        accessToken: body['accessToken'] as String,
        refreshToken: body['refreshToken'] as String,
        accessTokenExpiresAt: DateTime.parse(body['accessTokenExpiresAt'] as String).toUtc(),
      ));

      completer.complete(true);
      return true;
    } catch (_) {
      await _tokenStore.clear();
      completer.complete(false);
      return false;
    } finally {
      _refreshInFlight = null;
    }
  }
}

/// Server-side pagination envelope.
class Paged<T> {
  const Paged({
    required this.items,
    required this.page,
    required this.pageSize,
    required this.totalCount,
    required this.totalPages,
  });

  final List<T> items;
  final int page;
  final int pageSize;
  final int totalCount;
  final int totalPages;

  bool get hasNext => page < totalPages;

  factory Paged.fromJson(Map<String, dynamic> json, T Function(Map<String, dynamic>) fromItem) {
    return Paged(
      items: ((json['items'] as List?) ?? const [])
          .map((e) => fromItem(e as Map<String, dynamic>))
          .toList(),
      page: json['page'] as int? ?? 1,
      pageSize: json['pageSize'] as int? ?? 20,
      totalCount: json['totalCount'] as int? ?? 0,
      totalPages: json['totalPages'] as int? ?? 1,
    );
  }

  static Paged<T> empty<T>() =>
      Paged<T>(items: const [], page: 1, pageSize: 20, totalCount: 0, totalPages: 1);
}

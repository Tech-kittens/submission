class ApiException implements Exception {
  const ApiException(this.message, [this.status, this.code, this.traceId]);
  final String message;
  final int? status;
  final String? code;
  final String? traceId;
  @override
  String toString() => message;
}

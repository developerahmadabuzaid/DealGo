/// The auction policies a person reads and accepts every time they join an auction, as written in
/// the dashboard.
class AuctionPolicy {
  const AuctionPolicy({
    required this.title,
    required this.body,
    required this.version,
    required this.required,
  });

  final String title;

  /// Heading lines and paragraphs, one per line, in the same form as the terms document.
  final String body;

  /// Sent back with the join, so the server knows which wording was accepted.
  final int version;

  /// False while no policies are published: joining then asks for nothing.
  final bool required;

  factory AuctionPolicy.fromJson(Map<String, dynamic> j) => AuctionPolicy(
        title: j['title'] as String? ?? '',
        body: j['body'] as String? ?? '',
        version: (j['version'] as num?)?.toInt() ?? 0,
        required: j['required'] as bool? ?? false,
      );
}

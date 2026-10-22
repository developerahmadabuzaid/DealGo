/// Wire models for the auction API.
///
/// Every value here is decided by the backend. The client stores and displays them; it never
/// derives a rank, a winner or an end time of its own. The one figure it works out is the
/// discounted prize price shown before an auction ends ([prizePriceAt]); that mirrors the server's
/// rule, and what a winner actually owes is still the amount the server puts on the claim.
library;

DateTime? _date(dynamic v) => v == null ? null : DateTime.parse(v as String).toUtc();
double _num(dynamic v) => (v as num?)?.toDouble() ?? 0;

/// What a prize costs its winner when the auction price stands at [auctionPrice].
///
/// The auction's discount — where its price sits against its value — applies to every prize alike,
/// not only the first. Same rule as the server's AuctionRules.PrizePaymentAmount.
double prizePriceAt(
  double prizePrice,
  double auctionPrice,
  double auctionValue,
) {
  if (auctionValue <= 0 || auctionPrice <= 0) return prizePrice;
  final fraction = (auctionPrice / auctionValue).clamp(0.0, 1.0);
  return (prizePrice * fraction * 100).roundToDouble() / 100;
}

/// The discount, 0..1, that an auction price of [auctionPrice] gives on a value of [auctionValue].
double discountAt(double auctionPrice, double auctionValue) {
  if (auctionValue <= 0 || auctionPrice <= 0) return 0;
  return (1 - auctionPrice / auctionValue).clamp(0.0, 1.0);
}

class AuctionSummary {
  const AuctionSummary({
    required this.id,
    required this.auctionNumber,
    required this.title,
    required this.mainProductName,
    required this.mainImageUrl,
    required this.originalPrice,
    required this.startingPrice,
    required this.currentPrice,
    required this.targetPrice,
    required this.entryFee,
    required this.currency,
    required this.participantCount,
    required this.requiredParticipants,
    required this.status,
    required this.scheduledStartAt,
    required this.actualStartedAt,
    required this.remainingSeconds,
    this.coverImageUrl,
    this.isFeatured = false,
    this.winnerName,
    this.finishedAt,
    this.hasJoined = false,
  });

  final String id;
  final String auctionNumber;
  final String title;
  final String mainProductName;
  final String? mainImageUrl;

  /// The auction's own picture. When there is none, the first prize's picture stands in.
  final String? coverImageUrl;
  String? get imageUrl => coverImageUrl ?? mainImageUrl;

  /// Picked by an administrator for the front page's featured row.
  final bool isFeatured;

  /// Who stood highest when a finished auction closed, masked. Null while it runs, or when nobody bid.
  final String? winnerName;
  final DateTime? finishedAt;

  /// The signed-in user is a participant.
  final bool hasJoined;

  final double originalPrice;
  final double startingPrice;
  final double currentPrice;
  final double targetPrice;
  final double entryFee;
  final String currency;
  final int participantCount;
  final int requiredParticipants;
  final String status;
  final DateTime? scheduledStartAt;
  final DateTime? actualStartedAt;
  final int? remainingSeconds;

  bool get isLive => status == 'Live' || status == 'FinalPhase';
  bool get isFinalPhase => status == 'FinalPhase';
  bool get acceptsRegistration =>
      status == 'RegistrationOpen' || status == 'WaitingForParticipants';
  bool get isComplete => const {
        'Finished', 'WinnersDeclared', 'PrizeClaim', 'Completed'
      }.contains(status);

  int get participantsRemaining =>
      (requiredParticipants - participantCount).clamp(0, requiredParticipants);

  double get fillProgress =>
      requiredParticipants == 0 ? 0 : (participantCount / requiredParticipants).clamp(0, 1);

  factory AuctionSummary.fromJson(Map<String, dynamic> j) => AuctionSummary(
        id: j['id'] as String,
        auctionNumber: j['auctionNumber'] as String? ?? '',
        title: j['title'] as String? ?? '',
        mainProductName: j['mainProductName'] as String? ?? '',
        mainImageUrl: j['mainImageUrl'] as String?,
        originalPrice: _num(j['originalPrice']),
        startingPrice: _num(j['startingPrice']),
        currentPrice: _num(j['currentPrice']),
        targetPrice: _num(j['targetPrice']),
        entryFee: _num(j['entryFee']),
        currency: j['currency'] as String? ?? 'USD',
        participantCount: j['participantCount'] as int? ?? 0,
        requiredParticipants: j['requiredParticipants'] as int? ?? 0,
        status: j['status'] as String? ?? 'Draft',
        scheduledStartAt: _date(j['scheduledStartAt']),
        actualStartedAt: _date(j['actualStartedAt']),
        remainingSeconds: j['remainingSeconds'] as int?,
        coverImageUrl: j['coverImageUrl'] as String?,
        isFeatured: j['isFeatured'] as bool? ?? false,
        winnerName: j['winnerName'] as String?,
        finishedAt: _date(j['finishedAt']),
        hasJoined: j['hasJoined'] as bool? ?? false,
      );
}

class AuctionPrize {
  const AuctionPrize({
    required this.id,
    required this.rank,
    required this.productName,
    required this.imageUrl,
    required this.originalPrice,
    required this.currency,
    this.description,
  });

  final String id;
  final int rank;
  final String productName;
  final String? imageUrl;
  final double originalPrice;
  final String currency;
  final String? description;

  factory AuctionPrize.fromJson(Map<String, dynamic> j) => AuctionPrize(
        id: j['id'] as String,
        rank: j['rank'] as int? ?? 0,
        productName: j['productName'] as String? ?? '',
        imageUrl: j['imageUrl'] as String?,
        originalPrice: _num(j['originalPrice']),
        currency: j['currency'] as String? ?? 'USD',
        description: j['description'] as String?,
      );
}

class AuctionDetail {
  const AuctionDetail({
    required this.id,
    required this.auctionNumber,
    required this.title,
    required this.description,
    required this.mainProductName,
    required this.mainImageUrl,
    required this.originalPrice,
    required this.startingPrice,
    required this.currentPrice,
    required this.targetPrice,
    required this.finalPhasePrice,
    required this.bidIncrement,
    required this.entryFee,
    required this.currency,
    required this.participantCount,
    required this.requiredParticipants,
    required this.startDelaySeconds,
    required this.durationSeconds,
    required this.participantsCompletedAt,
    required this.scheduledStartAt,
    required this.actualStartedAt,
    required this.finishedAt,
    required this.endsAt,
    required this.remainingSeconds,
    required this.status,
    required this.targetReached,
    required this.progress,
    required this.prizes,
    required this.hasJoined,
    required this.myRank,
    required this.myHighestPrice,
    required this.myBidCount,
    required this.serverTime,
    this.coverImageUrl,
  });

  final String id;
  final String auctionNumber;
  final String title;
  final String? description;
  final String mainProductName;
  final String? mainImageUrl;

  /// The auction's own picture. When there is none, the first prize's picture stands in.
  final String? coverImageUrl;
  String? get imageUrl => coverImageUrl ?? mainImageUrl;
  final double originalPrice;
  final double startingPrice;
  final double currentPrice;
  final double targetPrice;
  final double finalPhasePrice;
  final double bidIncrement;
  final double entryFee;
  final String currency;
  final int participantCount;
  final int requiredParticipants;
  final int startDelaySeconds;
  final int durationSeconds;
  final DateTime? participantsCompletedAt;
  final DateTime? scheduledStartAt;
  final DateTime? actualStartedAt;
  final DateTime? finishedAt;
  final DateTime? endsAt;
  final int? remainingSeconds;
  final String status;
  final bool targetReached;
  final double progress;
  final List<AuctionPrize> prizes;
  final bool hasJoined;
  final int? myRank;
  final double? myHighestPrice;
  final int? myBidCount;

  /// The server's clock at the moment this was produced, used to correct for device clock drift.
  final DateTime serverTime;

  bool get isLive => status == 'Live' || status == 'FinalPhase';
  bool get isFinalPhase => status == 'FinalPhase';
  bool get acceptsRegistration =>
      status == 'RegistrationOpen' || status == 'WaitingForParticipants';
  bool get isComplete => const {
        'Finished', 'WinnersDeclared', 'PrizeClaim', 'Completed'
      }.contains(status);
  bool get isWaitingToStart =>
      status == 'Ready' || status == 'WaitingForStart';

  int get participantsRemaining =>
      (requiredParticipants - participantCount).clamp(0, requiredParticipants);

  factory AuctionDetail.fromJson(Map<String, dynamic> j) => AuctionDetail(
        id: j['id'] as String,
        auctionNumber: j['auctionNumber'] as String? ?? '',
        title: j['title'] as String? ?? '',
        description: j['description'] as String?,
        mainProductName: j['mainProductName'] as String? ?? '',
        mainImageUrl: j['mainImageUrl'] as String?,
        originalPrice: _num(j['originalPrice']),
        startingPrice: _num(j['startingPrice']),
        currentPrice: _num(j['currentPrice']),
        targetPrice: _num(j['targetPrice']),
        finalPhasePrice: _num(j['finalPhasePrice']),
        bidIncrement: _num(j['bidIncrement']),
        entryFee: _num(j['entryFee']),
        currency: j['currency'] as String? ?? 'USD',
        participantCount: j['participantCount'] as int? ?? 0,
        requiredParticipants: j['requiredParticipants'] as int? ?? 0,
        startDelaySeconds: j['startDelayAfterParticipantsSeconds'] as int? ?? 0,
        durationSeconds: j['durationSeconds'] as int? ?? 0,
        participantsCompletedAt: _date(j['participantsCompletedAt']),
        scheduledStartAt: _date(j['scheduledStartAt']),
        actualStartedAt: _date(j['actualStartedAt']),
        finishedAt: _date(j['finishedAt']),
        endsAt: _date(j['endsAt']),
        remainingSeconds: j['remainingSeconds'] as int?,
        status: j['status'] as String? ?? 'Draft',
        targetReached: j['targetReached'] as bool? ?? false,
        progress: _num(j['progress']),
        prizes: ((j['prizes'] as List?) ?? const [])
            .map((e) => AuctionPrize.fromJson(e as Map<String, dynamic>))
            .toList(),
        hasJoined: j['hasJoined'] as bool? ?? false,
        myRank: j['myRank'] as int?,
        myHighestPrice: (j['myHighestPrice'] as num?)?.toDouble(),
        myBidCount: j['myBidCount'] as int?,
        serverTime: _date(j['serverTime']) ?? DateTime.now().toUtc(),
        coverImageUrl: j['coverImageUrl'] as String?,
      );
}

/// The authoritative state a reconnecting client resynchronises against.
class AuctionSnapshot {
  const AuctionSnapshot({
    required this.id,
    required this.status,
    required this.currentPrice,
    required this.startingPrice,
    required this.targetPrice,
    required this.finalPhasePrice,
    required this.bidIncrement,
    required this.currency,
    required this.participantCount,
    required this.requiredParticipants,
    required this.remainingSeconds,
    required this.endsAt,
    required this.targetReached,
    required this.progress,
    required this.myRank,
    required this.myHighestPrice,
    required this.leaderboard,
    required this.lastSequenceNumber,
    required this.serverTime,
    this.myParticipantId,
    this.lastBidParticipantId,
  });

  final String id;
  final String status;
  final double currentPrice;
  final double startingPrice;
  final double targetPrice;
  final double finalPhasePrice;
  final double bidIncrement;
  final String currency;
  final int participantCount;
  final int requiredParticipants;
  final int? remainingSeconds;
  final DateTime? endsAt;
  final bool targetReached;
  final double progress;
  final int? myRank;
  final double? myHighestPrice;
  final List<LeaderboardEntry> leaderboard;
  final int lastSequenceNumber;
  final DateTime serverTime;

  /// This user's participant id in the auction, when they have joined it.
  final String? myParticipantId;

  /// Who placed the latest bid. Nobody may bid twice in a row, so while this is the user the bid
  /// button stays off until someone else bids.
  final String? lastBidParticipantId;

  bool get isLive => status == 'Live' || status == 'FinalPhase';

  bool get lastBidIsMine =>
      myParticipantId != null && lastBidParticipantId == myParticipantId;

  AuctionSnapshot copyWith({
    double? currentPrice,
    bool? targetReached,
    double? progress,
    int? participantCount,
    List<LeaderboardEntry>? leaderboard,
    String? lastBidParticipantId,
  }) =>
      AuctionSnapshot(
        id: id, status: status, currentPrice: currentPrice ?? this.currentPrice,
        startingPrice: startingPrice, targetPrice: targetPrice,
        finalPhasePrice: finalPhasePrice, bidIncrement: bidIncrement, currency: currency,
        participantCount: participantCount ?? this.participantCount,
        requiredParticipants: requiredParticipants,
        remainingSeconds: remainingSeconds, endsAt: endsAt,
        targetReached: targetReached ?? this.targetReached,
        progress: progress ?? this.progress,
        myRank: myRank, myHighestPrice: myHighestPrice,
        leaderboard: leaderboard ?? this.leaderboard,
        lastSequenceNumber: lastSequenceNumber, serverTime: serverTime,
        myParticipantId: myParticipantId,
        lastBidParticipantId: lastBidParticipantId ?? this.lastBidParticipantId,
      );
  bool get isFinalPhase => status == 'FinalPhase';

  factory AuctionSnapshot.fromJson(Map<String, dynamic> j) => AuctionSnapshot(
        id: j['id'] as String,
        status: j['status'] as String? ?? 'Draft',
        currentPrice: _num(j['currentPrice']),
        startingPrice: _num(j['startingPrice']),
        targetPrice: _num(j['targetPrice']),
        finalPhasePrice: _num(j['finalPhasePrice']),
        bidIncrement: _num(j['bidIncrement']),
        currency: j['currency'] as String? ?? 'USD',
        participantCount: j['participantCount'] as int? ?? 0,
        requiredParticipants: j['requiredParticipants'] as int? ?? 0,
        remainingSeconds: j['remainingSeconds'] as int?,
        endsAt: _date(j['endsAt']),
        targetReached: j['targetReached'] as bool? ?? false,
        progress: _num(j['progress']),
        myRank: j['myRank'] as int?,
        myHighestPrice: (j['myHighestPrice'] as num?)?.toDouble(),
        leaderboard: ((j['leaderboard'] as List?) ?? const [])
            .map((e) => LeaderboardEntry.fromJson(e as Map<String, dynamic>))
            .toList(),
        lastSequenceNumber: (j['lastSequenceNumber'] as num?)?.toInt() ?? 0,
        serverTime: _date(j['serverTime']) ?? DateTime.now().toUtc(),
        myParticipantId: j['myParticipantId'] as String?,
        lastBidParticipantId: j['lastBidParticipantId'] as String?,
      );
}

class LeaderboardEntry {
  const LeaderboardEntry({
    required this.rank,
    required this.participantId,
    required this.displayName,
    required this.highestPrice,
    required this.bidCount,
    required this.isMe,
  });

  final int rank;
  final String participantId;
  final String displayName;
  final double highestPrice;
  final int bidCount;
  final bool isMe;

  factory LeaderboardEntry.fromJson(Map<String, dynamic> j) => LeaderboardEntry(
        rank: j['rank'] as int? ?? 0,
        participantId: j['participantId'] as String? ?? '',
        displayName: j['displayName'] as String? ?? '',
        highestPrice: _num(j['highestPrice']),
        bidCount: j['bidCount'] as int? ?? 0,
        isMe: j['isMe'] as bool? ?? false,
      );
}

/// One accepted bid in an auction's public log.
class BidHistoryEntry {
  const BidHistoryEntry({
    required this.sequenceNumber,
    required this.price,
    required this.displayName,
    required this.placedAt,
    required this.isMe,
  });

  final int sequenceNumber;
  final double price;
  final String displayName;
  final DateTime? placedAt;
  final bool isMe;

  factory BidHistoryEntry.fromJson(Map<String, dynamic> j) => BidHistoryEntry(
        sequenceNumber: (j['sequenceNumber'] as num?)?.toInt() ?? 0,
        price: _num(j['price']),
        displayName: j['displayName'] as String? ?? '',
        placedAt: _date(j['placedAt']),
        isMe: j['isMe'] as bool? ?? false,
      );
}

class BidResult {
  const BidResult({
    required this.bidId,
    required this.sequenceNumber,
    required this.previousPrice,
    required this.newPrice,
    required this.targetPrice,
    required this.reachedTarget,
    required this.finalPhase,
    required this.auctionStatus,
    required this.myRank,
    required this.myHighestPrice,
    required this.wasDuplicate,
  });

  final String bidId;
  final int sequenceNumber;
  final double previousPrice;
  final double newPrice;
  final double targetPrice;
  final bool reachedTarget;
  final bool finalPhase;
  final String auctionStatus;
  final int myRank;
  final double myHighestPrice;
  final bool wasDuplicate;

  factory BidResult.fromJson(Map<String, dynamic> j) => BidResult(
        bidId: j['bidId'] as String,
        sequenceNumber: (j['sequenceNumber'] as num?)?.toInt() ?? 0,
        previousPrice: _num(j['previousPrice']),
        newPrice: _num(j['newPrice']),
        targetPrice: _num(j['targetPrice']),
        reachedTarget: j['reachedTarget'] as bool? ?? false,
        finalPhase: j['finalPhase'] as bool? ?? false,
        auctionStatus: j['auctionStatus'] as String? ?? '',
        myRank: j['myRank'] as int? ?? 0,
        myHighestPrice: _num(j['myHighestPrice']),
        wasDuplicate: j['wasDuplicate'] as bool? ?? false,
      );
}

class AuctionWinner {
  const AuctionWinner({
    required this.rank,
    required this.userId,
    required this.displayName,
    required this.highestPrice,
    required this.prizeName,
    required this.prizeImageUrl,
    required this.prizeValue,
    required this.currency,
    required this.prizeClaimId,
    required this.claimStatus,
    this.paymentAmount,
  });

  final int rank;
  final String userId;
  final String displayName;
  final double highestPrice;
  final String prizeName;
  final String? prizeImageUrl;
  final double prizeValue;
  final String currency;
  final String? prizeClaimId;
  final String? claimStatus;

  /// What the winner pays: [prizeValue] after the auction's discount. Null from a server that
  /// does not send it yet.
  final double? paymentAmount;

  factory AuctionWinner.fromJson(Map<String, dynamic> j) => AuctionWinner(
        rank: j['rank'] as int? ?? 0,
        userId: j['userId'] as String? ?? '',
        displayName: j['displayName'] as String? ?? '',
        highestPrice: _num(j['highestPrice']),
        prizeName: j['prizeName'] as String? ?? '',
        prizeImageUrl: j['prizeImageUrl'] as String?,
        prizeValue: _num(j['prizeValue']),
        currency: j['currency'] as String? ?? 'USD',
        prizeClaimId: j['prizeClaimId'] as String?,
        claimStatus: j['claimStatus'] as String?,
        paymentAmount: j['paymentAmount'] == null ? null : _num(j['paymentAmount']),
      );
}

class MyParticipation {
  const MyParticipation({
    required this.participantId,
    required this.auctionId,
    required this.entryFeePaid,
    required this.currency,
    required this.highestPrice,
    required this.currentRank,
    required this.bidCount,
    required this.status,
  });

  final String participantId;
  final String auctionId;
  final double entryFeePaid;
  final String currency;
  final double highestPrice;
  final int currentRank;
  final int bidCount;
  final String status;

  factory MyParticipation.fromJson(Map<String, dynamic> j) => MyParticipation(
        participantId: j['participantId'] as String,
        auctionId: j['auctionId'] as String,
        entryFeePaid: _num(j['entryFeePaid']),
        currency: j['currency'] as String? ?? 'USD',
        highestPrice: _num(j['highestPrice']),
        currentRank: j['currentRank'] as int? ?? 0,
        bidCount: j['bidCount'] as int? ?? 0,
        status: j['status'] as String? ?? '',
      );
}

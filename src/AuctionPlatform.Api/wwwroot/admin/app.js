/*
 * Auction Platform admin dashboard.
 *
 * A dependency-free SPA served straight out of wwwroot, so the admin panel ships with the API and
 * needs no separate build step or deployment. It calls the same /api/v1 surface as the mobile
 * client; authorisation is enforced server side on every request.
 */
'use strict';

const API = '/api/v1';

const state = {
  auth: null,          // { accessToken, refreshToken, user }
  route: 'dashboard',
  cache: {},
  // Set only while the auction form is reopened on an existing draft. Null means "create".
  editAuctionId: null,
};

// ---------------------------------------------------------------- storage

/*
 * Which sidebar groups are folded shut.
 *
 * Kept outside `state` because render() rebuilds the whole sidebar on every navigation, so the
 * open/closed choice has to survive that — and a reload, which is why it is written to storage.
 * A group absent from the object counts as open, so a new group added later starts visible.
 */
const navFolds = {
  load() {
    try {
      const raw = localStorage.getItem('auction.admin.nav');
      return raw ? JSON.parse(raw) : {};
    } catch { return {}; }
  },
  save(folds) {
    try { localStorage.setItem('auction.admin.nav', JSON.stringify(folds)); } catch { /* private mode */ }
  },
};

const store = {
  load() {
    try {
      const raw = localStorage.getItem('auction.admin.auth');
      return raw ? JSON.parse(raw) : null;
    } catch { return null; }
  },
  save(auth) {
    try { localStorage.setItem('auction.admin.auth', JSON.stringify(auth)); } catch { /* private mode */ }
  },
  clear() {
    try { localStorage.removeItem('auction.admin.auth'); } catch { /* ignore */ }
  },
};

// ---------------------------------------------------------------- api client

/**
 * Calls the API with the current access token, transparently refreshing it once on a 401 so a
 * short-lived token never interrupts an admin mid-task.
 */
async function api(path, options = {}, retry = true) {
  // A file upload sends FormData, and the browser has to write that header itself so it can put
  // the multipart boundary in it. Declaring JSON over the top would make the body unreadable.
  const isForm = options.body instanceof FormData;

  const headers = {
    ...(isForm ? {} : { 'Content-Type': 'application/json' }),
    ...(options.headers || {}),
  };
  if (state.auth?.accessToken) headers.Authorization = `Bearer ${state.auth.accessToken}`;

  const res = await fetch(`${API}${path}`, { ...options, headers });

  if (res.status === 401 && retry && state.auth?.refreshToken) {
    const refreshed = await tryRefresh();
    if (refreshed) return api(path, options, false);
    logout();
    throw new Error('Session expired.');
  }

  if (res.status === 204) return null;

  const text = await res.text();
  const body = text ? JSON.parse(text) : null;

  if (!res.ok) {
    // Our own errors carry `message`. A request ASP.NET refuses before it reaches a controller — a
    // body over the size limit, say — answers with ProblemDetails instead, which has `errors` and
    // `title`; reading only `message` left nothing but the status code on screen.
    const problem = body?.errors ? Object.values(body.errors).flat()[0] : null;
    const err = new Error(body?.message || problem || body?.title || `فشل الطلب (${res.status})`);
    err.code = body?.code;
    err.details = body?.details || [];
    throw err;
  }

  return body;
}

async function tryRefresh() {
  try {
    const res = await fetch(`${API}/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: state.auth.refreshToken }),
    });
    if (!res.ok) return false;

    const auth = await res.json();
    state.auth = auth;
    store.save(auth);
    return true;
  } catch { return false; }
}

function logout() {
  state.auth = null;
  store.clear();
  render();
}

// ---------------------------------------------------------------- arabic

/*
 * The dashboard is Arabic. Rather than rewrite every call site, the translation happens
 * where text becomes a DOM node — see `h` below.
 *
 * This matters for correctness, not only for effort. Words like "Draft", "Paid" and
 * "Active" are BOTH labels on screen AND values the API expects. Translating at render
 * time changes only what the user reads: a status posted back to the server, an <option>
 * value, or a class name is never touched, because those never become text nodes.
 *
 * A word with no entry here falls through unchanged, so untranslated text shows up as
 * English rather than as a blank cell.
 */
const AR = {
  // navigation and page titles
  'Overview': 'نظرة عامة', 'Dashboard': 'لوحة التحكم', 'Auctions': 'المزادات',
  'Website': 'الموقع', 'Page content': 'محتوى الصفحة', 'Steps & cards': 'الخطوات والبطاقات',
  'Reviews': 'آراء المستخدمين', 'Questions': 'الأسئلة الشائعة',
  'App ads': 'إعلانات التطبيق',
  'All auctions': 'كل المزادات', 'Create auction': 'إنشاء مزاد', 'Store': 'المتجر',
  'Products': 'المنتجات', 'Categories': 'التصنيفات', 'Inventory': 'المخزون',
  'Orders': 'الطلبات', 'Settings': 'الإعدادات', 'Finance': 'المالية',
  'Wallets': 'المحافظ', 'Shopping credits': 'أرصدة التسوق', 'People': 'المستخدمون',
  'Users': 'المستخدمون', 'Audit log': 'سجل التدقيق', 'Auction Platform': 'DealGo',
  'Admin console': 'لوحة الإدارة', 'Sign out': 'تسجيل الخروج', 'Sign in': 'تسجيل الدخول',
  'Store overview': 'نظرة عامة على المتجر', 'Store products': 'منتجات المتجر',
  'Store orders': 'طلبات المتجر', 'Store & wallets': 'المتجر والمحافظ',

  // common actions
  'Save': 'حفظ', 'Close': 'إغلاق', 'Apply': 'تطبيق', 'Edit': 'تعديل', 'View': 'عرض',
  'Search': 'بحث', 'Next': 'التالي', 'Prev': 'السابق', 'Adjust': 'تعديل الرصيد',
  'Remove': 'حذف', 'Activate': 'تفعيل', 'Disable': 'إيقاف', 'Archive': 'أرشفة', 'Hide': 'إخفاء',
  'Restart': 'إعادة تشغيل', 'Add product': 'إضافة منتج', 'Add category': 'إضافة تصنيف',
  'Edit category': 'تعديل التصنيف', 'Edit start time': 'تعديل وقت البدء',
  'Create as draft': 'حفظ كمسودة', 'Issue credit': 'منح رصيد', 'OK': 'حسناً',
  'Mark shipped': 'تحديد كمشحون', 'Mark delivered': 'تحديد كمُسلَّم',
  'Mark completed': 'تحديد كمكتمل', 'Alert': 'تنبيه',

  // table and form labels
  'Number': 'الرقم', 'Title': 'العنوان', 'Status': 'الحالة', 'Name': 'الاسم',
  'Email': 'البريد الإلكتروني', 'Password': 'كلمة المرور', 'Price': 'السعر',
  'Stock': 'المخزون', 'Category': 'التصنيف', 'Product': 'المنتج', 'Order': 'الطلب',
  'Customer': 'العميل', 'Total': 'الإجمالي', 'Subtotal': 'المجموع الفرعي',
  'Tax': 'الضريبة', 'Tax %': 'نسبة الضريبة %', 'Qty': 'الكمية', 'Items': 'العناصر',
  'Type': 'النوع', 'Value': 'القيمة', 'Created': 'تاريخ الإنشاء', 'Actor': 'المنفِّذ',
  'IP': 'عنوان IP', 'User': 'المستخدم', 'Roles': 'الصلاحيات', 'Wallet': 'المحفظة',
  'Ledger': 'كشف الحساب', 'Bids': 'المزايدات', 'Highest': 'الأعلى', 'Claim': 'المطالبة',
  'Prize': 'الجائزة', 'Prize value': 'قيمة الجائزة', 'Prizes': 'الجوائز',
  'Target': 'المستهدف', 'Target %': 'نسبة المستهدف %', 'Slug': 'المعرف',
  'SKU': 'رمز المنتج', 'Display order': 'ترتيب العرض', 'Featured': 'مميّز',
  'Main department': 'القسم الرئيسي', 'Sub-departments': 'الأقسام الفرعية',
  'Only two levels are allowed: choose a main department as the parent.':
    'مسموح بمستويين فقط: اختر قسماً رئيسياً.',
  'This department has sub-departments of its own, so it cannot become a sub-department.':
    'هذا القسم له أقسام فرعية، فلا يمكن جعله قسماً فرعياً.',
  'A department cannot sit under itself.': 'لا يمكن وضع القسم تحت نفسه.',
  'Move or delete this department\'s sub-departments first.':
    'انقل الأقسام الفرعية لهذا القسم أو احذفها أولاً.',
  'Main department not found.': 'القسم الرئيسي غير موجود.',
  'Image URL': 'رابط الصورة', 'Primary image URL': 'رابط الصورة الرئيسية', 'Product image': 'صورة المنتج',
  'Price before offer': 'السعر قبل الخصم', 'Offer': 'العرض',
  'Start delay': 'مهلة البدء', 'Scheduled start (local time)': 'وقت البدء (التوقيت المحلي)',
  'Low stock threshold': 'حد المخزون المنخفض', 'Expires after (days)': 'ينتهي بعد (أيام)',
  'Maximum order': 'الحد الأقصى للطلب', 'Last activity': 'آخر نشاط',
  'Availability': 'التوفر', 'Withdrawable': 'قابل للسحب', 'From': 'من', 'To': 'إلى',
  'Before': 'قبل', 'After': 'بعد', 'Paid with': 'تم الدفع بـ', 'Pays with': 'يدفع بـ',
  'System': 'النظام', 'All': 'الكل', 'All stock': 'كل المخزون',
  'Both wallets': 'المحفظتان', 'Yes': 'نعم', 'No': 'لا',

  // statuses — labels here, never the value sent back to the API
  'Draft': 'مسودة', 'Scheduled': 'مجدول', 'RegistrationOpen': 'التسجيل مفتوح',
  'WaitingForParticipants': 'بانتظار المشاركين', 'Ready': 'جاهز',
  'WaitingForStart': 'بانتظار البدء', 'Live': 'مباشر', 'FinalPhase': 'المرحلة الأخيرة',
  'Finished': 'منتهي', 'WinnersDeclared': 'تم إعلان الفائزين', 'PrizeClaim': 'استلام الجوائز',
  'Completed': 'مكتمل', 'Paused': 'متوقف مؤقتاً', 'Cancelled': 'ملغي', 'Expired': 'منتهي الصلاحية',
  'Active': 'نشط', 'Archived': 'مؤرشف', 'Disabled': 'موقوف', 'Available': 'متاح',
  'Reserved': 'محجوز', 'Sold': 'مباع', 'Used': 'مستخدم', 'Issued': 'صادر',
  'Out of stock': 'نفد المخزون', 'OutOfStock': 'نفد المخزون', 'Low stock': 'مخزون منخفض',
  'Paid': 'مدفوع', 'Shipped': 'تم الشحن', 'Delivered': 'تم التسليم',
  'Pending': 'قيد الانتظار', 'Processing': 'قيد المعالجة', 'Main': 'الرئيسية',
  'Shopping': 'التسوق',

  // messages
  'Nothing to show.': 'لا توجد بيانات.', 'No categories yet.': 'لا توجد تصنيفات بعد.',
  'No catalog products': 'لا توجد منتجات', 'Session expired.': 'انتهت الجلسة.',
  'Product saved.': 'تم حفظ المنتج.', 'Product updated.': 'تم تحديث المنتج.',
  'Category saved.': 'تم حفظ التصنيف.', 'Stock adjusted.': 'تم تعديل المخزون.',
  'Order marked shipped.': 'تم تحديد الطلب كمشحون.', 'Roles updated.': 'تم تحديث الصلاحيات.',
  'User updated.': 'تم تحديث المستخدم.', 'Wallet adjusted.': 'تم تعديل المحفظة.',
  'Platform-wide figures, straight from the database.':
    'أرقام المنصة كاملةً، مباشرةً من قاعدة البيانات.',

  // auction form and detail
  'Create auction': 'إنشاء مزاد', 'Administration': 'الإدارة', 'Manage': 'إدارة',
  '1 · Basic information': '١ · المعلومات الأساسية', '2 · Prizes': '٢ · الجوائز',
  '3 · Pricing': '٣ · التسعير', '4 · Participation & timing': '٤ · المشاركة والتوقيت',
  'Description': 'الوصف', 'Original price': 'السعر الأصلي', 'Starting price': 'سعر البداية',
  'Current price': 'السعر الحالي', 'Bid increment': 'قيمة المزايدة',
  'Final phase %': 'نسبة المرحلة الأخيرة %', 'Entry fee': 'رسوم الاشتراك',
  'Required participants': 'عدد المشاركين المطلوب', 'Duration': 'المدة',
  'Duration (seconds)': 'المدة (بالثواني)', 'Expiration policy': 'سياسة الانتهاء',
  'Start delay after target reached (seconds)': 'مهلة البدء بعد اكتمال العدد (بالثواني)',
  'Prize #1 — main product (competes on price)': 'الجائزة ١ — المنتج الرئيسي (المنافسة على سعره)',
  'Prize #2 — second place': 'الجائزة ٢ — المركز الثاني',
  'Rank': 'المركز', 'Winner': 'الفائز', 'Participant': 'المشارك', 'Joined': 'تاريخ الانضمام',
  'Event': 'الحدث', 'When': 'الوقت', 'Unit': 'سعر الوحدة', 'Amount': 'المبلغ',
  'Balance after': 'الرصيد بعد', 'Open': 'مفتوح', 'Enabled': 'مفعّل',
  'Main wallet': 'المحفظة الرئيسية', 'Shopping credit': 'رصيد التسوق',
  'Shipping': 'الشحن', 'Shipping address': 'عنوان الشحن', 'Reason': 'السبب',
  'Cancel': 'إلغاء', 'Confirm': 'تأكيد', 'Loading…': 'جارٍ التحميل…',
  'Save settings': 'حفظ الإعدادات', 'Low stock only': 'المخزون المنخفض فقط',
  'Reason (recorded in the audit log)': 'السبب (يُسجَّل في سجل التدقيق)',
  'Recorded in the audit log': 'يُسجَّل في سجل التدقيق',
  'New inventory shipment': 'شحنة مخزون جديدة',

  // longer explanatory lines
  'An auction needs exactly four ranked prizes before it can be published.':
    'يحتاج المزاد إلى أربع جوائز مرتبة بالضبط قبل نشره.',
  'Publish, schedule and start auctions. Every action is audited.':
    'نشر المزادات وجدولتها وبدؤها. كل إجراء يُسجَّل في سجل التدقيق.',
  'Append-only record of every administrative and financial action.':
    'سجل لا يقبل التعديل لكل إجراء إداري ومالي.',
  'Append-only record of everything that happened to this auction.':
    'سجل لا يقبل التعديل لكل ما جرى في هذا المزاد.',
  'Bid history is immutable and cannot be edited from here.':
    'سجل المزايدات ثابت ولا يمكن تعديله من هنا.',
  'Ranked by highest price, then by the earliest server timestamp at which it was reached.':
    'الترتيب حسب أعلى سعر، ثم حسب أسبق وقت وصل إليه على الخادم.',
  'Main and Shopping balances are always reported separately.':
    'رصيد المحفظة الرئيسية ورصيد التسوق يُعرضان منفصلين دائماً.',
  'The closed loop: entry fees in, store purchases out.':
    'الدورة المغلقة: رسوم الاشتراك تدخل، ومشتريات المتجر تخرج.',
  'Store purchases only · never withdrawable':
    'للشراء من المتجر فقط · غير قابل للسحب',
  'Products referenced by an order are archived rather than deleted.':
    'المنتجات المرتبطة بطلب تُؤرشف ولا تُحذف.',
  'Stock only moves alongside an inventory transaction.':
    'لا يتغير المخزون إلا مع حركة مخزون مسجّلة.',
  'Add prize products first; an auction needs exactly four.':
    'أضف منتجات الجوائز أولاً؛ يحتاج المزاد إلى أربعة بالضبط.',
  'Winners have not been declared yet.': 'لم يتم إعلان الفائزين بعد.',

  // placeholders
  'Name or SKU': 'الاسم أو رمز المنتج', 'Name or email': 'الاسم أو البريد الإلكتروني',
  'Name, SKU or category': 'الاسم أو الرمز أو التصنيف',
  'Name, email or phone': 'الاسم أو البريد الإلكتروني أو الهاتف',
  'Search title or number': 'ابحث بالعنوان أو الرقم',
  'Order number or customer': 'رقم الطلب أو العميل',
  'Actor email or reason': 'بريد المنفِّذ أو السبب',
  'Optional': 'اختياري', 'Participants': 'المشاركون',

  // dashboard figures
  'Active auctions': 'مزادات نشطة', 'Upcoming auctions': 'مزادات قادمة',
  'Completed auctions': 'مزادات مكتملة', 'Total participants': 'إجمالي المشاركين',
  // expiration policy, shown as a label in the auction detail
  'EndAndDistribute': 'إنهاء وتوزيع الجوائز',
  'Extend': 'تمديد المدة',
  'Restart': 'إعادة التشغيل',

  // auction lifecycle timeline
  'Lifecycle': 'مراحل المزاد',
  'Participants completed': 'اكتمل عدد المشاركين',
  'Scheduled start': 'موعد البدء المحدد',
  'Auction started': 'انطلق المزاد',
  'Final phase': 'المرحلة النهائية',
  'Auction ready': 'المزاد جاهز',
  'Entry fees': 'رسوم الاشتراك',
  'Total entry revenue': 'إجمالي إيرادات الاشتراك',
  "Today's entry revenue": 'إيرادات الاشتراك اليوم',
  'Pending prize payments': 'مدفوعات جوائز معلّقة',
  'Pending prize value': 'قيمة الجوائز المعلّقة',
  'Shopping credit (closed loop)': 'رصيد التسوق (الدورة المغلقة)',
  // Says what the figure is rather than what it is called in accounting: the total store
  // credit sitting unspent across every user's wallet.
  'Outstanding liability': 'رصيد التسوق الحالي في جميع المحافظ',
  // The bare word was missing, so the store overview and the credits page printed it in English.
  'Outstanding': 'رصيد التسوق الحالي في جميع المحافظ',
  'Remaining': 'المتبقي',
  'Used in store': 'المستخدم في المتجر',
  'Store & wallets ': 'المتجر والمحافظ', 'Main wallet deposits': 'إيداعات المحفظة الرئيسية',
  'Main wallet withdrawals': 'سحوبات المحفظة الرئيسية', 'Store revenue': 'إيرادات المتجر',
  'Store spend': 'الإنفاق في المتجر', 'Pending orders': 'طلبات معلّقة',
  'Auction refunds': 'مبالغ مستردة من المزادات', 'Event log': 'سجل الأحداث',
  'Last login': 'آخر دخول', 'Initial stock': 'المخزون الافتتاحي',

  // auction actions
  'Start now': 'ابدأ الآن', 'Cancel auction': 'إلغاء المزاد',
  'Auction published.': 'تم نشر المزاد.', 'Auction started.': 'تم بدء المزاد.',
  'Auction paused.': 'تم إيقاف المزاد مؤقتاً.', 'Auction resumed.': 'تم استئناف المزاد.',
  'Auction cancelled.': 'تم إلغاء المزاد.',
  'Start time updated. Participants were notified.':
    'تم تحديث وقت البدء. وأُبلغ المشاركون.',
  'Participants will be notified and every entry fee is returned to the Main wallet as cash.':
    'سيتم إبلاغ المشاركين وإعادة رسوم الاشتراك بالكامل إلى المحفظة الرئيسية نقداً.',
  'Moves the price; it is not charged to the bidder.':
    'تحرّك السعر فقط، ولا تُخصم من المزايد.',
  'Charged once from the Main wallet.': 'تُخصم مرة واحدة من المحفظة الرئيسية.',
  'End and distribute (recommended)': 'الإنهاء والتوزيع (موصى به)',
  'Any status': 'كل الحالات',

  // orders
  'Mark processing': 'تحديد كقيد المعالجة', 'Cancel order': 'إلغاء الطلب',
  'Refund order': 'استرداد الطلب', 'Cancel & refund': 'إلغاء واسترداد',
  'Tracking number': 'رقم التتبع', 'Accepted payment': 'الدفع المقبول',
  'Order cancelled and refunded.': 'تم إلغاء الطلب واسترداد المبلغ.',
  'Order refunded.': 'تم استرداد مبلغ الطلب.',
  'Adding a tracking number is optional.': 'إضافة رقم التتبع اختيارية.',
  'Stock is returned and the money goes back to the wallet it came from.':
    'تعود الكمية إلى المخزون ويعود المبلغ إلى المحفظة التي خرج منها.',

  // store settings
  'Store settings': 'إعدادات المتجر', 'Settings saved.': 'تم حفظ الإعدادات.',
  'Shipping fee': 'رسوم الشحن', 'Default shipping fee': 'رسوم الشحن الافتراضية',
  'Minimum order': 'الحد الأدنى للطلب', 'Credit expiration': 'انتهاء صلاحية الرصيد',
  'Allow main wallet purchases': 'السماح بالشراء من المحفظة الرئيسية',
  'Allow shopping wallet purchases': 'السماح بالشراء من محفظة التسوق',
  'Change (+ to restock, − to remove)': 'التغيير (+ للإضافة، − للخصم)',

  // wallets and users
  'Main (withdrawable)': 'الرئيسية (قابلة للسحب)',
  'Shopping (store only)': 'التسوق (للمتجر فقط)',
  'Main wallet only': 'المحفظة الرئيسية فقط', 'Shopping wallet only': 'محفظة التسوق فقط',
  'Shopping wallet': 'محفظة التسوق', 'Reason (required)': 'السبب (مطلوب)',
  'Enable account': 'تفعيل الحساب', 'Disable account': 'إيقاف الحساب',
  'Disabling revokes every active session.': 'الإيقاف ينهي كل الجلسات النشطة.',
  'The user will be able to sign in again.': 'سيتمكن المستخدم من الدخول مجدداً.',
  'Comma-separated. Only SUPER_ADMIN may change roles.':
    'مفصولة بفواصل. المدير الأعلى فقط يمكنه تغيير الصلاحيات.',
  'This account does not have administrative access.':
    'هذا الحساب لا يملك صلاحية الدخول إلى لوحة الإدارة.',
};

const tr = (v) => (typeof v === 'string' && AR[v] !== undefined ? AR[v] : v);

// ---------------------------------------------------------------- helpers

const h = (tag, attrs = {}, ...children) => {
  const el = document.createElement(tag);
  for (const [k, v] of Object.entries(attrs)) {
    if (v === null || v === undefined || v === false) continue;
    if (k === 'class') el.className = v;
    else if (k === 'html') el.innerHTML = v;
    else if (k.startsWith('on') && typeof v === 'function') el.addEventListener(k.slice(2), v);
    // `value` is deliberately absent: an <option> shows Arabic but still posts English.
    else if (k === 'placeholder' || k === 'title') el.setAttribute(k, tr(v));
    else el.setAttribute(k, v);
  }
  for (const child of children.flat()) {
    if (child === null || child === undefined || child === false) continue;
    el.append(child instanceof Node ? child : document.createTextNode(String(tr(child))));
  }
  return el;
};

/*
 * Inline icons.
 *
 * Drawn here rather than pulled from a CDN: the admin panel ships inside the API image and has to
 * work on a machine with no internet access. Each entry is the inside of a 24x24 stroked <svg>,
 * so every icon shares one weight and one grid.
 */
const ICONS = {
  grid: '<rect x="3.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="13.5" y="3.5" width="7" height="7" rx="1.6"/><rect x="3.5" y="13.5" width="7" height="7" rx="1.6"/><rect x="13.5" y="13.5" width="7" height="7" rx="1.6"/>',
  gavel: '<path d="M3.5 20.5h9"/><path d="m6 15 6-6"/><path d="m9.5 5.5 5 5"/><path d="m13 2 9 9"/>',
  plus: '<circle cx="12" cy="12" r="8.5"/><path d="M12 8.5v7M8.5 12h7"/>',
  clock: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.5V12l3 2"/>',
  calendar: '<rect x="3.5" y="5" width="17" height="15.5" rx="2.2"/><path d="M8 3v4M16 3v4M3.5 10h17"/>',
  check: '<circle cx="12" cy="12" r="8.5"/><path d="m8.5 12.2 2.4 2.4 4.6-5"/>',
  users: '<circle cx="9.5" cy="8" r="3.3"/><path d="M3.8 19.2a5.7 5.7 0 0 1 11.4 0"/><path d="M16.4 5.2a3.3 3.3 0 0 1 0 5.6"/><path d="M18.4 19.2a5.7 5.7 0 0 0-2-4.4"/>',
  wallet: '<path d="M3.5 8.6A2.6 2.6 0 0 1 6.1 6h11.3a2 2 0 0 1 2 2v.6"/><rect x="3.5" y="8.6" width="17" height="10.9" rx="2.6"/><circle cx="16.4" cy="14" r="1.15"/>',
  gift: '<rect x="3.5" y="9.2" width="17" height="10.8" rx="2.2"/><path d="M3.5 13.4h17M12 9.2V20"/><path d="M12 9.2S10.7 5.2 8.6 5.2a2 2 0 1 0 0 4z"/><path d="M12 9.2s1.3-4 3.4-4a2 2 0 1 1 0 4z"/>',
  cart: '<circle cx="9.5" cy="19.8" r="1.35"/><circle cx="17" cy="19.8" r="1.35"/><path d="M2.8 4h2.3l2.2 10.1a1.9 1.9 0 0 0 1.9 1.5h7.3a1.9 1.9 0 0 0 1.9-1.5L20.4 8H6"/>',
  coins: '<circle cx="12" cy="12" r="8.5"/><path d="M12 7.2v9.6"/><path d="M14.4 9.6c0-1-1-1.7-2.4-1.7s-2.4.7-2.4 1.8c0 2.5 4.8 1.4 4.8 3.9 0 1.1-1 1.8-2.4 1.8s-2.4-.7-2.4-1.7"/>',
  box: '<path d="m12 3 8.2 4.3v9.4L12 21l-8.2-4.3V7.3z"/><path d="m3.8 7.3 8.2 4.4 8.2-4.4M12 21v-9.3"/>',
  tag: '<path d="M11.2 3.5H20v8.8l-8.6 8.6a1.7 1.7 0 0 1-2.4 0l-6.4-6.4a1.7 1.7 0 0 1 0-2.4z"/><circle cx="16.3" cy="7.7" r="1.3"/>',
  layers: '<path d="m12 3 8.5 4.4L12 11.8 3.5 7.4z"/><path d="m3.5 12 8.5 4.4 8.5-4.4"/><path d="m3.5 16.6 8.5 4.4 8.5-4.4"/>',
  receipt: '<path d="M5.5 3.2h13v17.6l-2.2-1.4-2.2 1.4-2.2-1.4-2.2 1.4-2.2-1.4-2.2 1.4z"/><path d="M9 8.2h6M9 12.2h6"/>',
  alert: '<path d="M12 4.2 2.9 19.8h18.2z"/><path d="M12 10v4M12 17.2h.01"/>',
  shield: '<path d="M12 3.2 4.8 6.1v5.3c0 4.2 2.9 7.6 7.2 9.4 4.3-1.8 7.2-5.2 7.2-9.4V6.1z"/><path d="m9.2 12 2 2 3.6-3.8"/>',
  sliders: '<path d="M4 7.5h9M17.5 7.5H20M4 16.5h3.5M12.5 16.5H20"/><circle cx="15.2" cy="7.5" r="2"/><circle cx="10" cy="16.5" r="2"/>',
  logout: '<path d="M9.5 4.5h-3a2 2 0 0 0-2 2v11a2 2 0 0 0 2 2h3"/><path d="M15 8.5 18.5 12 15 15.5"/><path d="M18.5 12H9"/>',
  chevron: '<path d="m6.5 9.5 5.5 5.5 5.5-5.5"/>',
  globe: '<circle cx="12" cy="12" r="8.5"/><path d="M3.5 12h17"/><path d="M12 3.5c2.2 2.3 3.4 5.3 3.4 8.5s-1.2 6.2-3.4 8.5c-2.2-2.3-3.4-5.3-3.4-8.5S9.8 5.8 12 3.5z"/>',
  quote: '<path d="M9.5 6.5C6.7 7.8 5 10.2 5 13.2V17h5v-5H7.6c.2-1.6 1.1-2.9 2.6-3.7z"/><path d="M18.5 6.5c-2.8 1.3-4.5 3.7-4.5 6.7V17h5v-5h-2.4c.2-1.6 1.1-2.9 2.6-3.7z"/>',
  help: '<circle cx="12" cy="12" r="8.5"/><path d="M9.7 9.6a2.4 2.4 0 1 1 3.1 2.6c-.6.2-.8.7-.8 1.3v.4"/><path d="M12 17.2h.01"/>',
  image: '<rect x="3.5" y="4.5" width="17" height="15" rx="2.2"/><circle cx="8.7" cy="9.7" r="1.5"/><path d="m4.2 17.4 4.8-4.4 3.2 2.9 2.8-2.4 4.6 4"/>',
  info: '<circle cx="12" cy="12" r="8.5"/><path d="M12 11v5.5"/><path d="M12 7.8h.01"/>',
  arrowUp: '<path d="M12 19V5"/><path d="m6 11 6-6 6 6"/>',
  arrowDown: '<path d="M12 5v14"/><path d="m6 13 6 6 6-6"/>',
};

/** One icon as an inline SVG node. An unknown name renders nothing rather than a broken glyph. */
function icon(name, cls = '') {
  const body = ICONS[name];
  if (!body) return null;

  const el = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  el.setAttribute('viewBox', '0 0 24 24');
  el.setAttribute('fill', 'none');
  el.setAttribute('stroke', 'currentColor');
  el.setAttribute('stroke-width', '1.6');
  el.setAttribute('stroke-linecap', 'round');
  el.setAttribute('stroke-linejoin', 'round');
  el.setAttribute('aria-hidden', 'true');
  el.setAttribute('class', ('ico ' + cls).trim());
  el.innerHTML = body;
  return el;
}

/**
 * A labelled progress bar. The percentage is clamped at 100 because a participant count is
 * allowed to pass its target.
 */
function progressBar(value, total, { label = null, text = null } = {}) {
  const pct = total > 0 ? Math.min(100, Math.round((value / total) * 100)) : 0;

  return h('div', { class: 'progress-block' },
    (label || text)
      ? h('div', { class: 'progress-head' },
          label ? h('span', { class: 'progress-label' }, label) : null,
          text ? h('span', { class: 'progress-figure' }, text) : null)
      : null,
    h('div', {
      class: 'progress',
      role: 'progressbar',
      'aria-valuenow': String(pct),
      'aria-valuemin': '0',
      'aria-valuemax': '100',
    }, h('div', { class: 'progress-fill', style: 'width:' + pct + '%' })),
    h('div', { class: 'progress-pct' }, pct + '%'));
}

/** The same bar with no caption, sized for a table cell. */
function miniProgress(value, total) {
  const pct = total > 0 ? Math.min(100, Math.round((value / total) * 100)) : 0;
  return h('div', { class: 'progress mini' },
    h('div', { class: 'progress-fill', style: 'width:' + pct + '%' }));
}

/**
 * The auction's lifecycle, drawn only from the timestamps the API already returns.
 *
 * A step counts as reached only when its own timestamp exists, so the timeline never claims a
 * stage the auction has not actually got to. Nothing here predicts or infers a date.
 */
function timeline(a) {
  const steps = [
    { label: 'Participants completed', at: a.participantsCompletedAt },
    { label: 'Scheduled start', at: a.scheduledStartAt },
    { label: 'Auction started', at: a.actualStartedAt },
    { label: 'Final phase', at: a.finalPhaseStartedAt },
    { label: 'Finished', at: a.finishedAt },
  ];

  const lastDone = steps.reduce((acc, s, i) => (s.at ? i : acc), -1);

  return h('ol', { class: 'timeline' }, ...steps.map((s, i) => h('li', {
    class: ['tl-step', s.at ? 'done' : 'todo', i === lastDone ? 'current' : ''].filter(Boolean).join(' '),
  },
    h('span', { class: 'tl-dot' }),
    h('div', { class: 'tl-body' },
      h('div', { class: 'tl-label' }, s.label),
      h('div', { class: 'tl-time' }, when(s.at))))));
}

/** Grey placeholder blocks, shown while a page's data is still in flight. */
function skeletonPage() {
  const bar = (w, ht = 14) => h('div', { class: 'skel', style: 'width:' + w + ';height:' + ht + 'px' });
  const gap = (ht) => h('div', { style: 'height:' + ht + 'px' });

  return h('div', {},
    h('div', { class: 'page-hero' },
      h('div', { class: 'hero-top' },
        h('div', { class: 'skel', style: 'width:46px;height:46px;border-radius:12px;flex:none' }),
        h('div', { style: 'flex:1' }, bar('180px', 24), gap(10), bar('280px')))),
    h('div', { class: 'kpi-grid' }, ...Array.from({ length: 4 }, () =>
      h('div', { class: 'kpi' }, bar('58%', 11), gap(14), bar('45%', 26)))),
    h('div', { class: 'card' }, bar('140px', 16), gap(18),
      ...Array.from({ length: 5 }, () => h('div', { style: 'padding:10px 0' }, bar('100%')))));
}

const CURRENCY_SYMBOLS = { SAR: '', AED: 'د.إ', KWD: 'د.ك', EGP: 'ج.م', USD: '$', EUR: '€' };
const symbolFor = (c) => CURRENCY_SYMBOLS[String(c || '').toUpperCase()] || c || '';

const digits = (v) =>
  Number(v).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });

/** The plain string, for the few places that need text rather than an element. */
const moneyText = (v, c = 'SAR') =>
  v === null || v === undefined ? '—' : `${digits(v)} ${symbolFor(c)}`;

/*
 * An amount, built as an element rather than a string.
 *
 * Two things a single text node could not do.
 *
 * Order: Arabic puts the currency after the amount, and "after" in a right-to-left line means to
 * the LEFT of the digits. The whole amount therefore reads right-to-left while the digits stay a
 * left-to-right run of their own, which is what the <bdi> is for. Written as one string and
 * forced left-to-right, the symbol landed on the right, so an Arabic reader met it first.
 *
 * Size: the symbol is a unit, not part of the figure. At 24px beside a headline number it
 * competed with the digits, so it is set smaller and lighter here. Only markup can do that;
 * CSS cannot reach half of a text node.
 */
function money(v, c = 'SAR') {
  if (v === null || v === undefined) return '—';

  return h('span', { class: 'amount' },
    h('bdi', { class: 'amount-num' }, digits(v)),
    h('span', { class: 'amount-cur' }, symbolFor(c)));
}

const num = (v) => (v === null || v === undefined ? '—' : Number(v).toLocaleString());

/*
 * A timestamp, wrapped in Unicode isolate marks.
 *
 * A date is a left-to-right run. Dropped bare into Arabic text it gets reordered by the bidi
 * algorithm, so '9/11/2026, 12:30 PM' renders with the comma and the AM/PM in the wrong places.
 * U+2066 and U+2069 isolate the run so it reads correctly wherever it is placed, including inside
 * a template literal where a CSS direction rule could not reach it.
 */
const when = (v) => (v ? '⁦' + new Date(v).toLocaleString() + '⁩' : '—');

const shortId = (v) => (v ? String(v).slice(0, 8) : '—');

function badgeClass(status) {
  const s = String(status || '').toLowerCase();
  if (['live', 'finalphase', 'active', 'completed', 'paid', 'delivered', 'ok'].includes(s)) return 'badge live';
  if (['ready', 'waitingforstart', 'registrationopen', 'waitingforparticipants', 'processing', 'shipped', 'paymentrequired'].includes(s)) return 'badge info';
  if (['draft', 'pending', 'hidden', 'outofstock', 'scheduled'].includes(s)) return 'badge warn';
  if (['cancelled', 'expired', 'refunded', 'archived', 'failed'].includes(s)) return 'badge danger';
  return 'badge';
}

function toast(message, kind = '') {
  document.querySelectorAll('.toast').forEach((t) => t.remove());
  const el = h('div', { class: `toast ${kind}` }, message);
  document.body.append(el);
  setTimeout(() => el.remove(), 4200);
}

function confirmDialog(title, message, onConfirm, { danger = false, needsReason = false } = {}) {
  const reason = h('input', { placeholder: 'Reason (recorded in the audit log)' });
  const back = h('div', { class: 'modal-back' });

  const submit = async () => {
    if (needsReason && !reason.value.trim()) { reason.focus(); return; }
    back.remove();
    await onConfirm(reason.value.trim());
  };

  back.append(h('div', { class: 'modal' },
    h('h3', {}, title),
    h('p', { class: 'sub' }, message),
    needsReason ? reason : null,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Cancel'),
      h('button', { class: danger ? 'btn danger' : 'btn', onclick: submit }, 'Confirm'))));

  document.body.append(back);
  if (needsReason) reason.focus();
}

function formModal(title, subtitle, fields, onSubmit) {
  const inputs = {};
  const back = h('div', { class: 'modal-back' });
  const error = h('div', { class: 'err-text' });

  // Uploads still in flight; saving waits for them so the form never saves the old picture.
  let uploading = 0;

  const body = fields.map((f) => {
    if (f.type === 'image') {
      // The picture is uploaded as soon as it is picked, so the form holds an address, the same
      // way the prize dialog does.
      const holder = h('input', { type: 'hidden', value: f.value ?? '' });
      inputs[f.name] = holder;

      const thumb = h('div', { class: 'prize-thumb' });
      const fileInput = h('input', { type: 'file', accept: 'image/*' });
      fileInput.hidden = true;
      const pick = h('button', { class: 'btn ghost sm', type: 'button' }, 'اختر صورة');
      const remove = h('button', { class: 'btn ghost sm', type: 'button' }, 'إزالة الصورة');
      const state = h('div', { class: 'hint' }, 'JPEG أو PNG أو GIF · حتى ٥ ميجابايت');

      const paint = () => {
        thumb.replaceChildren(holder.value ? h('img', { src: holder.value, alt: '' }) : icon('box'));
        thumb.className = holder.value ? 'prize-thumb' : 'prize-thumb empty';
        remove.disabled = holder.value ? null : '';
      };

      pick.addEventListener('click', () => fileInput.click());
      remove.addEventListener('click', () => { holder.value = ''; paint(); state.textContent = 'لا توجد صورة'; });

      fileInput.addEventListener('change', async () => {
        const file = fileInput.files?.[0];
        if (!file) return;

        error.textContent = '';
        pick.disabled = '';
        state.textContent = 'جارٍ الرفع…';
        uploading += 1;

        try {
          holder.value = await uploadImage(file);
          paint();
          state.textContent = 'تم رفع الصورة';
        } catch (e) {
          state.textContent = '';
          error.textContent = e.message;
        } finally {
          uploading -= 1;
          pick.disabled = null;
          // Clearing lets the same file be picked again after a failure.
          fileInput.value = '';
        }
      });

      paint();

      return h('div', {}, h('label', {}, f.label),
        h('div', { class: 'row' }, thumb,
          h('div', {}, h('div', { class: 'row' }, pick, remove), state)),
        fileInput);
    }

    if (f.type === 'select') {
      const sel = h('select', {}, ...f.options.map((o) =>
        h('option', { value: o.value, selected: o.value === f.value ? '' : null }, o.label)));
      inputs[f.name] = sel;
      return h('div', {}, h('label', {}, f.label), sel, f.hint ? h('div', { class: 'hint' }, f.hint) : null);
    }
    const input = h('input', {
      type: f.type || 'text',
      value: f.value ?? '',
      placeholder: f.placeholder || '',
      step: f.step || null,
    });
    inputs[f.name] = input;
    return h('div', {}, h('label', {}, f.label), input, f.hint ? h('div', { class: 'hint' }, f.hint) : null);
  });

  const save = async () => {
    const values = {};
    if (uploading) { error.textContent = 'انتظر حتى يكتمل رفع الصورة.'; return; }
    for (const [k, el] of Object.entries(inputs)) values[k] = el.value;
    error.textContent = '';
    try {
      await onSubmit(values);
      back.remove();
    } catch (e) {
      error.textContent = tr(e.message) + (e.details?.length ? ` (${e.details.join('; ')})` : '');
    }
  };

  back.append(h('div', { class: 'modal' },
    h('h3', {}, title),
    subtitle ? h('p', { class: 'sub' }, subtitle) : null,
    ...body,
    error,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Cancel'),
      h('button', { class: 'btn', onclick: save }, 'Save'))));

  document.body.append(back);
}

/** Renders a paged table with a shared toolbar, so every list page behaves the same way. */
function pagedTable({ columns, rows, page, totalPages, totalCount, onPage }) {
  if (!rows.length) return h('div', { class: 'empty' }, 'Nothing to show.');

  const table = h('table', {},
    h('thead', {}, h('tr', {}, ...columns.map((c) => h('th', { class: c.num ? 'num' : '' }, c.label)))),
    h('tbody', {}, ...rows.map((r) => h('tr', {}, ...columns.map((c) => {
      const cell = c.render(r);
      const td = h('td', { class: [c.num ? 'num' : '', c.mono ? 'mono' : ''].filter(Boolean).join(' ') });

      // Plain cell text is DATA — a product name, a title, an email — so it is appended
      // directly and never passed through the Arabic dictionary. A row whose title happened
      // to match a UI label would otherwise be rewritten on screen. Cells that need
      // translating (a status badge, an action button) return a Node built with `h`, and
      // those were already translated when they were built.
      if (cell instanceof Node) td.append(cell);
      else td.append(document.createTextNode(String(cell ?? '—')));

      return td;
    })))));

  return h('div', {},
    h('div', { class: 'table-wrap' }, table),
    h('div', { class: 'pager' },
      `الإجمالي ${num(totalCount)}`,
      h('button', { class: 'btn ghost sm', disabled: page <= 1 ? '' : null, onclick: () => onPage(page - 1) }, 'Prev'),
      `صفحة ${page} من ${Math.max(totalPages, 1)}`,
      h('button', { class: 'btn ghost sm', disabled: page >= totalPages ? '' : null, onclick: () => onPage(page + 1) }, 'Next')));
}

// ---------------------------------------------------------------- login

/*
 * Staff sign in the way customers do: with their mobile number and a one-time code.
 *
 * The requests carry staffOnly, so a number that is not an active staff account gets the server's
 * decoy reply — no message is sent and no account is created — and nothing on this screen says
 * whether a number is registered. Email and password remain for accounts that have no number on
 * file, such as the development accounts.
 */
const LOGIN_ERRORS = {
  INVALID_OTP: 'رمز التحقق غير صحيح.',
  OTP_EXPIRED: 'انتهت صلاحية الرمز. اطلب رمزًا جديدًا.',
  OTP_ALREADY_USED: 'استُخدم هذا الرمز من قبل. اطلب رمزًا جديدًا.',
  OTP_TOO_MANY_ATTEMPTS: 'محاولات كثيرة. اطلب رمزًا جديدًا.',
  OTP_REQUEST_TOO_SOON: 'انتظر قليلًا قبل طلب رمز جديد.',
  OTP_DESTINATION_INVALID: 'أدخل رقم جوال سعوديًا صحيحًا، مثل 05XXXXXXXX.',
  VALIDATION_FAILED: 'أدخل رقم جوال سعوديًا صحيحًا، مثل 05XXXXXXXX.',
  FORBIDDEN: 'هذا الحساب لا يملك صلاحية الدخول إلى لوحة الإدارة.',
  ACCOUNT_DISABLED: 'هذا الحساب موقوف.',
  RATE_LIMITED: 'محاولات كثيرة. حاول بعد قليل.',
  INVALID_CREDENTIALS: 'البريد الإلكتروني أو كلمة المرور غير صحيحة.',
};

const loginError = (e) => LOGIN_ERRORS[e.code] || e.message;

function finishLogin(auth) {
  const isAdmin = (auth.user.roles || []).some((r) => r !== 'USER');
  if (!isAdmin) throw Object.assign(new Error(LOGIN_ERRORS.FORBIDDEN), { code: 'FORBIDDEN' });

  state.auth = auth;
  store.save(auth);
  render();
}

function loginCard(...body) {
  return h('div', { class: 'login-wrap' },
    h('div', { class: 'login-card' },
      h('div', { class: 'login-brand' }, brandMark()),
      h('h1', {}, brandWord()),
      h('p', { class: 'sub' }, 'صفقتك جاهزة · لوحة الإدارة'),
      ...body));
}

function renderLogin() {
  const card = h('div');
  const show = (view) => card.replaceChildren(view);

  // ---- step one: the number
  const phoneStep = () => {
    const phone = h('input', {
      type: 'tel', inputmode: 'numeric', autocomplete: 'tel-national', dir: 'ltr',
      placeholder: '5X XXX XXXX',
    });
    const error = h('div', { class: 'err-text' });
    const button = h('button', { class: 'btn', style: 'width:100%;margin-top:16px' }, 'إرسال رمز التحقق');

    const submit = async () => {
      error.textContent = '';
      const raw = phone.value.trim();
      if (!raw) { error.textContent = LOGIN_ERRORS.OTP_DESTINATION_INVALID; return; }

      // The country code sits beside the field; the server accepts the number however it is written.
      const destination = raw.startsWith('+') || raw.startsWith('00') ? raw : `+966${raw.replace(/^0/, '')}`;

      button.disabled = true;
      try {
        const challenge = await api('/auth/otp/request', {
          method: 'POST',
          body: JSON.stringify({ destination, language: 'ar', staffOnly: true }),
        });
        show(codeStep(destination, challenge));
      } catch (e) {
        error.textContent = loginError(e);
      } finally {
        button.disabled = false;
      }
    };

    button.addEventListener('click', submit);
    phone.addEventListener('keydown', (e) => { if (e.key === 'Enter') submit(); });
    setTimeout(() => phone.focus(), 0);

    return h('div', {},
      h('label', {}, 'رقم الجوال'),
      h('div', { class: 'phone-field' }, h('span', { class: 'cc', dir: 'ltr' }, '+966'), phone),
      h('p', { class: 'hint' }, 'يصلك رمز تحقق برسالة نصية إذا كان الرقم مسجّلًا لحساب إداري.'),
      button,
      error,
      h('button', { class: 'link-btn', onclick: () => show(emailStep()) }, 'الدخول بالبريد الإلكتروني وكلمة المرور'));
  };

  // ---- step two: the code
  const codeStep = (destination, challenge) => {
    const length = challenge.codeLength || 6;
    const code = h('input', {
      type: 'text', inputmode: 'numeric', autocomplete: 'one-time-code', dir: 'ltr',
      maxlength: String(length), class: 'code-input', placeholder: '•'.repeat(length),
    });
    // Outside Production the server echoes the code, so the flow can be used without SMS.
    if (challenge.developmentCode) code.value = challenge.developmentCode;

    const error = h('div', { class: 'err-text' });
    const button = h('button', { class: 'btn', style: 'width:100%;margin-top:16px' }, 'تأكيد');
    const resend = h('button', { class: 'link-btn' });

    let left = challenge.resendAvailableInSeconds || 60;
    const tick = () => {
      if (!resend.isConnected && left < (challenge.resendAvailableInSeconds || 60)) return;
      if (left > 0) {
        resend.textContent = `إعادة الإرسال بعد ${left} ثانية`;
        resend.disabled = true;
        left -= 1;
        setTimeout(tick, 1000);
      } else {
        resend.textContent = 'إعادة إرسال الرمز';
        resend.disabled = false;
      }
    };
    tick();

    resend.addEventListener('click', async () => {
      error.textContent = '';
      try {
        const again = await api('/auth/otp/request', {
          method: 'POST',
          body: JSON.stringify({ destination, language: 'ar', staffOnly: true }),
        });
        show(codeStep(destination, again));
      } catch (e) {
        error.textContent = loginError(e);
      }
    });

    const submit = async () => {
      error.textContent = '';
      button.disabled = true;
      try {
        const auth = await api('/auth/otp/verify', {
          method: 'POST',
          body: JSON.stringify({ destination, code: code.value.trim(), staffOnly: true }),
        });
        finishLogin(auth);
      } catch (e) {
        error.textContent = loginError(e);
      } finally {
        button.disabled = false;
      }
    };

    button.addEventListener('click', submit);
    code.addEventListener('keydown', (e) => { if (e.key === 'Enter') submit(); });
    code.addEventListener('input', () => {
      code.value = code.value.replace(/\D/g, '');
      if (code.value.length === length) submit();
    });
    setTimeout(() => code.focus(), 0);

    return h('div', {},
      h('p', { class: 'hint center' }, 'أدخل رمز التحقق المرسل إلى ',
        h('b', { dir: 'ltr' }, challenge.destination || destination)),
      code,
      button,
      error,
      h('div', { class: 'login-links' },
        resend,
        h('button', { class: 'link-btn', onclick: () => show(phoneStep()) }, 'تغيير الرقم')));
  };

  // ---- the other way in
  const emailStep = () => {
    const email = h('input', { type: 'email', dir: 'ltr', placeholder: 'name@example.com' });
    const password = h('input', { type: 'password', dir: 'ltr', placeholder: '••••••••' });
    const error = h('div', { class: 'err-text' });
    const button = h('button', { class: 'btn', style: 'width:100%;margin-top:16px' }, 'Sign in');

    const submit = async () => {
      error.textContent = '';
      button.disabled = true;
      try {
        const auth = await api('/auth/login', {
          method: 'POST',
          body: JSON.stringify({ email: email.value.trim(), password: password.value }),
        });
        finishLogin(auth);
      } catch (e) {
        error.textContent = loginError(e);
      } finally {
        button.disabled = false;
      }
    };

    button.addEventListener('click', submit);
    password.addEventListener('keydown', (e) => { if (e.key === 'Enter') submit(); });

    return h('div', {},
      h('label', {}, 'Email'), email,
      h('label', {}, 'Password'), password,
      button,
      error,
      h('button', { class: 'link-btn', onclick: () => show(phoneStep()) }, 'الدخول برقم الجوال'));
  };

  show(phoneStep());
  return loginCard(card);
}

// ---------------------------------------------------------------- shell

const NAV = [
  { group: 'Overview', items: [{ id: 'dashboard', label: 'Dashboard', icon: 'grid' }] },
  {
    group: 'Auctions',
    items: [
      { id: 'auctions', label: 'All auctions', icon: 'gavel' },
      { id: 'auction-new', label: 'Create auction', icon: 'plus' },
    ],
  },
  {
    group: 'Store',
    items: [
      { id: 'store-overview', label: 'Overview', icon: 'cart' },
      { id: 'store-products', label: 'Products', icon: 'box' },
      { id: 'store-categories', label: 'Categories', icon: 'layers' },
      { id: 'store-inventory', label: 'Inventory', icon: 'tag' },
      { id: 'store-orders', label: 'Orders', icon: 'receipt' },
      { id: 'store-settings', label: 'Settings', icon: 'sliders' },
    ],
  },
  {
    group: 'Finance',
    items: [
      { id: 'wallets', label: 'Wallets', icon: 'wallet' },
      { id: 'shopping-credits', label: 'Shopping credits', icon: 'gift' },
    ],
  },
  {
    group: 'Website',
    items: [
      { id: 'site-content', label: 'Page content', icon: 'globe' },
      { id: 'site-cards', label: 'Steps & cards', icon: 'layers' },
      { id: 'site-testimonials', label: 'Reviews', icon: 'quote' },
      { id: 'site-faqs', label: 'Questions', icon: 'help' },
      { id: 'app-ads', label: 'App ads', icon: 'image' },
    ],
  },
  {
    group: 'System',
    items: [
      { id: 'users', label: 'Users', icon: 'users' },
      { id: 'audit', label: 'Audit log', icon: 'shield' },
    ],
  },
];

function renderShell() {
  const nav = h('nav', { class: 'sidebar' },
    h('div', { class: 'brand' },
      brandMark(),
      h('span', { class: 'brand-text' },
        brandWord(),
        h('small', {}, 'صفقتك جاهزة'))));

  const folds = navFolds.load();

  for (const section of NAV) {
    const folded = folds[section.group] === true;
    const holdsCurrent = section.items.some((i) => i.id === state.route);

    const items = h('div', { class: 'nav-items' },
      ...section.items.map((item) => h('button', {
        class: `nav-item ${state.route === item.id ? 'active' : ''}`,
        // The edit id is dropped here, or clicking "create auction" would reopen the draft.
        onclick: () => { state.route = item.id; state.editAuctionId = null; render(); },
      }, icon(item.icon), h('span', {}, item.label))));

    // `hidden` rather than a class, so the mobile bar can override it in one rule and the
    // items stay reachable when the groups themselves are not shown.
    items.hidden = folded;

    // A folded group that holds the open page still says so, otherwise folding it loses the
    // only sign of where you are.
    const header = h('button', {
      class: `nav-group ${folded ? 'folded' : ''} ${folded && holdsCurrent ? 'holds-current' : ''}`,
      type: 'button',
      'aria-expanded': folded ? 'false' : 'true',
      onclick: () => {
        const next = navFolds.load();
        next[section.group] = !folded;
        navFolds.save(next);
        render();
      },
    }, h('span', {}, section.group), icon('chevron', 'nav-chevron'));

    nav.append(h('div', { class: 'nav-section' }, header, items));
  }

  // The name is data, so it is appended as a raw text node rather than translated.
  const who = h('div', { class: 'who' });
  who.append(
    h('span', { class: 'who-avatar' }, (state.auth.user.fullName || '?').trim().charAt(0)),
    h('span', { class: 'who-text' },
      h('strong', {}, document.createTextNode(state.auth.user.fullName || '')),
      h('small', {}, document.createTextNode(state.auth.user.roles.join(' · ')))));

  nav.append(h('div', { class: 'sidebar-footer' },
    who,
    h('button', { class: 'nav-item', onclick: logout }, icon('logout'), h('span', {}, 'Sign out'))));

  // A skeleton rather than the word "Loading": the page keeps its shape while the data arrives.
  const main = h('main', { class: 'main' }, skeletonPage());

  routeView(state.route)
    .then((view) => { main.replaceChildren(view); })
    .catch((e) => {
      main.replaceChildren(h('div', { class: 'card' },
        h('h3', {}, 'Could not load this page'),
        h('div', { class: 'err-text' }, e.message)));
    });

  return h('div', { class: 'shell' }, nav, main);
}

function routeView(route) {
  const views = {
    'dashboard': viewDashboard,
    'auctions': viewAuctions,
    'auction-new': viewAuctionForm,
    'auction-edit': viewAuctionForm,
    'store-overview': viewStoreOverview,
    'store-products': viewStoreProducts,
    'store-categories': viewStoreCategories,
    'store-inventory': viewInventory,
    'store-orders': viewOrders,
    'store-settings': viewStoreSettings,
    'wallets': viewWallets,
    'shopping-credits': viewShoppingCredits,
    'site-content': viewSiteContent,
    'site-cards': viewSiteCards,
    'site-testimonials': viewSiteTestimonials,
    'site-faqs': viewSiteFaqs,
    'app-ads': viewAppAds,
    'users': viewUsers,
    'audit': viewAudit,
  };
  return (views[route] || viewDashboard)();
}

/*
 * A route with no sidebar entry of its own, and the entry it borrows from.
 *
 * Editing a draft is the create form reopened, so it is introduced by the same symbol. Without
 * this the page would open under the fallback icon and look like a different kind of page.
 */
const ROUTE_ALIAS = { 'auction-edit': 'auction-new' };

/** The icon the sidebar uses for the page being shown, so the two always agree. */
function routeIcon() {
  const route = ROUTE_ALIAS[state.route] || state.route;

  for (const section of NAV) {
    for (const item of section.items) {
      if (item.id === route) return item.icon;
    }
  }
  return 'grid';
}

/**
 * Every page opens with the same panel: an icon, the title, the one sentence that explains the
 * page, and whatever actions belong to it. The icon is taken from the sidebar entry for the open
 * route, so a page can never be introduced by a different symbol than the one you clicked.
 */
function pageHead(title, subtitle, ...actions) {
  return h('div', { class: 'page-hero' },
    h('div', { class: 'hero-top' },
      h('span', { class: 'hero-icon' }, icon(routeIcon())),
      h('div', { class: 'hero-text' },
        h('h2', {}, title),
        subtitle ? h('div', { class: 'sub' }, subtitle) : null),
      actions.length ? h('div', { class: 'row' }, ...actions) : null));
}

/*
 * The DG monogram, drawn rather than loaded.
 *
 * This stands in until the artwork file is saved at /admin/logo.png, and it is what shows if that
 * file is ever missing. Speed lines and an arrow running into a black D, with an orange G, so the
 * placeholder carries the same idea as the real mark instead of an unrelated icon.
 */
const BRAND_MARK_SVG =
  '<g fill="#F26A1B">' +
  '<rect x="2" y="19" width="27" height="7" rx="3.5"/>' +
  '<rect x="0" y="30.5" width="19" height="7" rx="3.5"/>' +
  '<rect x="5" y="42" width="24" height="7" rx="3.5"/>' +
  '</g>' +
  '<path d="M16 34h40" stroke="#F26A1B" stroke-width="7" stroke-linecap="round" fill="none"/>' +
  '<path d="M50 23.5 L68 34 L50 44.5 Z" fill="#F26A1B"/>' +
  '<path fill="#1C1F24" fill-rule="evenodd" d="M34 5h20a29 29 0 0 1 0 58H34z' +
  'm13 13v32h7a16 16 0 0 0 0-32z"/>' +
  '<path fill="#F26A1B" d="M99 5a29 29 0 1 0 0 58 29 29 0 0 0 28-22H99V27h28.4A29 29 0 0 0 99 5z' +
  'm0 13a16 16 0 1 1 0 32 16 16 0 0 1 0-32z"/>';

/**
 * The DealGo mark.
 *
 * Uses the logo file at /brand/logo.png when it is present and falls back to the drawn monogram
 * if it is missing, so the console never shows a broken image while the artwork is being put in
 * place. Dropping the file in is all that is needed to switch.
 *
 * The address is /brand rather than /admin because the mobile app reads the very same file: one
 * logo for the whole product, in a folder whose name says so.
 */
function brandMark() {
  const box = h('span', { class: 'brand-mark' });

  const drawn = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  drawn.setAttribute('viewBox', '0 0 128 68');
  drawn.setAttribute('aria-hidden', 'true');
  drawn.setAttribute('class', 'brand-drawn');
  drawn.innerHTML = BRAND_MARK_SVG;

  const img = h('img', { src: '/brand/logo.png', alt: 'DealGo' });
  img.addEventListener('error', () => box.replaceChildren(drawn));

  box.append(img);
  return box;
}

/**
 * The page header for a long form.
 *
 * Carries the title, the one sentence that explains the page, and a row of steps that mirrors the
 * numbered sections below it. The steps are not decoration: each one reports whether that section
 * is actually complete, so the state of a four-part form is legible before scrolling it.
 *
 * Returns the element plus a `mark` function, which the form calls whenever something changes.
 */
function formHero({ title, subtitle, steps, action = null }) {
  const chips = steps.map((step) => {
    const badge = h('span', { class: 'step-badge' }, step.number);
    const note = h('span', { class: 'step-note' }, '');
    const chip = h('div', { class: 'step' },
      badge,
      h('span', { class: 'step-text' }, h('span', { class: 'step-label' }, step.label), note));
    return { chip, badge, note, step };
  });

  const el = h('div', { class: 'page-hero' },
    h('div', { class: 'hero-top' },
      h('span', { class: 'hero-icon' }, icon(routeIcon())),
      h('div', { class: 'hero-text' },
        h('h2', {}, title),
        h('div', { class: 'sub' }, subtitle)),
      action ? h('div', { class: 'row' }, action) : null),
    h('div', { class: 'steps' }, ...chips.map((c) => c.chip)));

  const mark = () => {
    for (const c of chips) {
      const state = c.step.state();
      c.chip.className = `step ${state.done ? 'done' : ''}`.trim();
      c.badge.replaceChildren(state.done ? icon('check') : document.createTextNode(c.step.number));
      c.note.textContent = state.note || '';
    }
  };

  mark();
  return { el, mark };
}

/**
 * A small info button that opens an explanation beside a field.
 *
 * For settings whose name cannot carry their meaning. `items` is a list of [term, explanation]
 * pairs, rendered as a description list so a screen reader reads each option with its own text.
 * One bubble is open at a time; clicking elsewhere or pressing Escape closes it.
 */
function infoHint(heading, items) {
  const panel = h('div', { class: 'info-panel', role: 'dialog' },
    h('div', { class: 'info-head' }, heading),
    h('dl', {},
      ...items.flatMap(([term, text]) => [h('dt', {}, term), h('dd', {}, text)])));
  panel.hidden = true;

  const btn = h('button', {
    class: 'info-btn',
    type: 'button',
    'aria-label': 'شرح الخيارات',
    'aria-expanded': 'false',
  }, icon('info'));

  const close = () => {
    panel.hidden = true;
    btn.setAttribute('aria-expanded', 'false');
    document.removeEventListener('click', onOutside, true);
    document.removeEventListener('keydown', onKey);
  };

  const onOutside = (e) => { if (!wrap.contains(e.target)) close(); };
  const onKey = (e) => { if (e.key === 'Escape') { close(); btn.focus(); } };

  btn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!panel.hidden) { close(); return; }

    // Only one explanation open at a time.
    document.querySelectorAll('.info-panel').forEach((x) => { x.hidden = true; });

    panel.hidden = false;
    btn.setAttribute('aria-expanded', 'true');
    document.addEventListener('click', onOutside, true);
    document.addEventListener('keydown', onKey);
  });

  const wrap = h('span', { class: 'info-wrap' }, btn, panel);
  return wrap;
}

/** The word DealGo, in the two brand colours. */
function brandWord() {
  return h('span', { class: 'brand-word' },
    h('span', { class: 'deal' }, 'Deal'),
    h('span', { class: 'go' }, 'Go'));
}

/** A prize picture at thumbnail size, or a placeholder when there is none. */
function prizeThumb(url) {
  if (!url) return h('div', { class: 'prize-thumb sm empty' }, icon('box'));

  const box = h('div', { class: 'prize-thumb sm' });
  const img = h('img', { src: url, alt: '' });
  img.addEventListener('error', () => {
    box.className = 'prize-thumb sm broken';
    box.replaceChildren(icon('alert'));
  });
  box.append(img);
  return box;
}

/**
 * A card's section heading: a tinted bar across the top of the card, carrying an icon and title.
 *
 * The tone is chosen to match the figures underneath — money in is green, money owed out is
 * amber, store credit is gold — so the colour repeats the meaning the icons already carry rather
 * than decorating at random. A card with no tone falls back to a plain grey bar.
 */
function sectionHead(title, iconName = null, tone = '') {
  return h('h3', { class: `card-head ${tone}`.trim() },
    iconName ? h('span', { class: 'card-head-icon' }, icon(iconName)) : null,
    h('span', {}, title));
}

/**
 * A month-on-month change pill.
 *
 * `change` is the {current, previous} pair the API returns for that figure, both measured from
 * real timestamps on the underlying rows. Nothing is estimated: when last month is zero there is
 * no percentage to state, so the pill says how much arrived this month instead of dividing by
 * zero or inventing a baseline.
 */
function trendPill(change) {
  if (!change) return null;

  const current = Number(change.current || 0);
  const previous = Number(change.previous || 0);

  if (previous === 0) {
    const text = current === 0 ? 'لا حركة هذا الشهر' : 'لا يوجد شهر سابق للمقارنة';
    return h('div', { class: 'trend' }, h('span', { class: 'trend-pill flat' }, text));
  }

  const pct = ((current - previous) / previous) * 100;
  const up = pct >= 0;
  const rounded = Math.abs(pct).toFixed(1);

  return h('div', { class: 'trend' },
    h('span', { class: `trend-pill ${up ? 'up' : 'down'}` },
      icon(up ? 'arrowUp' : 'arrowDown', 'trend-ico'),
      h('span', {}, `${up ? '+' : '−'}${rounded}%`)),
    h('span', { class: 'trend-note' }, 'مقارنة بالشهر الماضي'));
}

/**
 * One figure.
 *
 * `opts.icon` names an entry in ICONS and `opts.note` is a second, smaller fact underneath.
 * There is deliberately no trend indicator: the API reports the present only, and a percentage
 * "versus last month" would have to be invented.
 */
function kpi(label, value, isMoney = false, opts = {}) {
  const { icon: iconName = null, note = null, tone = '', change = null } = opts;

  return h('div', { class: 'kpi' },
    h('div', { class: 'kpi-top' },
      iconName ? h('span', { class: `kpi-icon ${tone}`.trim() }, icon(iconName)) : null,
      h('div', { class: 'label' }, label)),
    h('div', { class: `value ${isMoney ? 'money' : ''}` }, value),
    change ? trendPill(change) : null,
    note ? h('div', { class: 'kpi-note' }, note) : null);
}

// ---------------------------------------------------------------- dashboard

async function viewDashboard() {
  const d = await api('/admin/dashboard');

  const today = new Date().toLocaleDateString('ar-SA-u-ca-gregory-nu-latn',
    { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });

  return h('div', {},
    pageHead('Dashboard', 'Platform-wide figures, straight from the database.',
      h('span', { class: 'context-chip' }, icon('calendar'), h('span', {}, today))),

    // The four counts that describe the platform at a glance.
    h('div', { class: 'kpi-grid cols-4' },
      kpi('Active auctions', num(d.activeAuctions), false,
        { icon: 'gavel', tone: 'ok' }),
      kpi('Upcoming auctions', num(d.upcomingAuctions), false,
        { icon: 'calendar', tone: 'info' }),
      kpi('Completed auctions', num(d.completedAuctions), false,
        { icon: 'check', tone: 'neutral' }),
      kpi('Total participants', num(d.totalParticipants), false,
        { icon: 'users', tone: 'violet', change: d.participantsTrend })),

    // Entry fees and prizes stay separate headings, because money taken in and money still owed
    // out are different questions. They sit side by side rather than each taking a whole row.
    h('div', { class: 'section-row' },
      h('div', { class: 'card' },
        sectionHead('Entry fees', 'coins', 'ok'),
        h('div', { class: 'kpi-grid cols-2' },
          kpi('Total entry revenue', money(d.totalEntryRevenue, d.currency), true,
            { icon: 'coins', tone: 'ok', change: d.entryRevenueTrend }),
          kpi("Today's entry revenue", money(d.todayEntryRevenue, d.currency), true,
            { icon: 'clock', tone: 'info' }))),

      h('div', { class: 'card' },
        sectionHead('Prizes', 'gift', 'warn'),
        h('div', { class: 'kpi-grid cols-2' },
          kpi('Pending prize payments', num(d.pendingPrizePayments), false,
            { icon: 'gift', tone: 'warn' }),
          kpi('Pending prize value', money(d.pendingPrizeValue, d.currency), true,
            { icon: 'coins', tone: 'warn' })))),

    h('div', { class: 'card' },
      sectionHead('Shopping credit (closed loop)', 'gift', 'gold'),
      h('div', { class: 'kpi-grid cols-3' },
        kpi('Issued', money(d.shoppingCreditIssued, d.currency), true,
          { icon: 'gift', tone: 'gold', change: d.creditIssuedTrend }),
        kpi('Used in store', money(d.shoppingCreditUsed, d.currency), true,
          { icon: 'cart', tone: 'gold' }),
        kpi('Outstanding liability', money(d.shoppingCreditOutstanding, d.currency), true,
          { icon: 'alert', tone: 'warn' }))),

    h('div', { class: 'card' },
      sectionHead('Store & wallets', 'wallet', 'info'),
      h('div', { class: 'kpi-grid cols-5' },
        kpi('Store revenue', money(d.storeRevenue, d.currency), true,
          { icon: 'coins', tone: 'ok', change: d.storeRevenueTrend }),
        kpi('Store orders', num(d.storeOrders), false,
          { icon: 'receipt', tone: 'info' }),
        kpi('Pending orders', num(d.pendingStoreOrders), false,
          { icon: 'clock', tone: 'warn' }),
        kpi('Main wallet deposits', money(d.mainWalletDeposits, d.currency), true,
          { icon: 'wallet', tone: 'info' }),
        kpi('Main wallet withdrawals', money(d.mainWalletWithdrawals, d.currency), true,
          { icon: 'wallet', tone: 'neutral' }))));
}

// ---------------------------------------------------------------- auctions

async function viewAuctions() {
  const q = state.cache.auctions || (state.cache.auctions = { page: 1, group: '', search: '' });

  const load = async () => {
    const params = new URLSearchParams({ page: q.page, pageSize: 20 });
    if (q.group) params.set('group', q.group);
    if (q.search) params.set('search', q.search);
    return api(`/admin/auctions?${params}`);
  };

  const data = await load();

  const search = h('input', { placeholder: 'Search title or number', value: q.search });
  const group = h('select', {},
    // Written in Arabic here rather than left to the dictionary: "Upcoming" had no entry, so the
    // filter showed one English word among four Arabic ones. The values stay English because they
    // are what the API is asked for.
    ...[
      ['', 'الكل'],
      ['draft', 'قيد الإنشاء'],
      ['upcoming', 'القادم'],
      ['live', 'الحالي'],
      ['completed', 'المنتهي'],
    ].map(([v, l]) => h('option', { value: v, selected: v === q.group ? '' : null }, l)));

  const apply = () => { q.search = search.value; q.group = group.value; q.page = 1; render(); };

  return h('div', {},
    pageHead('Auctions', 'Publish, schedule and start auctions. Every action is audited.',
      h('button', { class: 'btn ghost', onclick: () => openFeaturedOrder() }, '★ ترتيب المميزة'),
      h('button', { class: 'btn', onclick: () => { state.route = 'auction-new'; render(); } }, 'Create auction')),

    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, group,
        h('button', { class: 'btn ghost sm', onclick: apply }, 'Apply')),

      pagedTable({
        columns: [
          {
            label: 'Number',
            mono: true,
            render: (a) => h('span', {},
              document.createTextNode(a.auctionNumber),
              a.isFeatured ? h('span', { class: 'star', title: 'مزاد مميز' }, ' ★') : null),
          },
          { label: 'Title', render: (a) => a.title },
          { label: 'Status', render: (a) => h('span', { class: badgeClass(a.status) }, a.status) },
          {
            label: 'Participants',
            render: (a) => h('div', { class: 'cell-progress' },
              h('span', { class: 'cell-progress-text' },
                `${a.participantCount} / ${a.requiredParticipants}`),
              miniProgress(a.participantCount, a.requiredParticipants)),
          },
          { label: 'Price', num: true, render: (a) => money(a.currentPrice, a.currency) },
          { label: 'Target', num: true, render: (a) => money(a.targetPrice, a.currency) },
          { label: 'Entry fee', num: true, render: (a) => money(a.entryFee, a.currency) },
          { label: 'Scheduled', render: (a) => when(a.scheduledStartAt) },
          { label: '', render: (a) => h('button', { class: 'btn ghost sm', onclick: () => openAuction(a.id) }, 'Manage') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function openAuction(id) {
  const a = await api(`/admin/auctions/${id}`);
  const back = h('div', { class: 'modal-back' });

  const act = async (fn, successMessage) => {
    try {
      await fn();
      back.remove();
      toast(successMessage, 'ok');
      render();
    } catch (e) {
      toast(e.message + (e.details?.length ? ` — ${e.details.join('; ')}` : ''), 'err');
    }
  };

  const canPublish = a.status === 'Draft' || a.status === 'Scheduled';
  const canStart = a.status === 'Ready' || a.status === 'WaitingForStart';
  const canPause = a.status === 'Live' || a.status === 'FinalPhase';
  const canResume = a.status === 'Paused';
  const canCancel = !['Completed', 'Cancelled'].includes(a.status);

  /*
   * One figure and its name, as a tile.
   *
   * The ten numbers this auction is described by were previously a label over a value, repeated
   * down two columns, which read as a wall of text. A grid of small tiles lets a price be found
   * by looking rather than by reading.
   */
  const stat = (label, value) => h('div', { class: 'stat' },
    h('div', { class: 'stat-label' }, label),
    h('div', { class: 'stat-value' }, value));

  /* A titled block. The tone colours the heading and the frame together, as a card does. */
  const section = (title, iconName, tone, ...body) =>
    h('section', { class: `modal-sec ${tone}` },
      h('h4', {}, h('span', { class: 'sec-icon' }, icon(iconName)), h('span', {}, title)),
      h('div', { class: 'sec-body' }, ...body));

  const readyPanel = canStart
    ? h('div', { class: 'panel ready' },
        h('h4', {}, icon('check'), h('span', {}, 'المزاد جاهز للبدء')),
        progressBar(a.participantCount, a.requiredParticipants, {
          label: 'المشاركون',
          text: `${a.participantCount} / ${a.requiredParticipants}`,
        }),
        h('div', { class: 'hint' }, `اكتمل عدد المشاركين: ${when(a.participantsCompletedAt)}`),
        h('div', { class: 'hint' }, `موعد البدء المحدد: ${when(a.scheduledStartAt)}`))
    : null;

  // Names and notes are data: appended as raw text so they are never put through the dictionary.
  const prizeRows = a.prizes.map((p) => {
    const nameCell = h('td', {});
    nameCell.append(document.createTextNode(p.productName || '—'));
    if (p.description) {
      nameCell.append(h('div', { class: 'hint' }, document.createTextNode(p.description)));
    }

    return h('tr', {},
      h('td', {}, h('span', { class: 'rank-chip' }, `#${p.rank}`)),
      h('td', {}, prizeThumb(p.imageUrl)),
      nameCell,
      h('td', { class: 'num' }, money(p.originalPrice, p.currency)));
  });

  /*
   * The dialog.
   *
   * Three fixed parts: a header that names the auction and its state, a middle that scrolls, and
   * a footer of actions that stays in view. The footer matters — the buttons that publish, start
   * or cancel an auction used to sit below four sections of figures and had to be scrolled to.
   */
  back.append(h('div', { class: 'modal modal-wide' },
    h('div', { class: 'modal-hero' },
      h('span', { class: 'modal-hero-icon' }, icon('gavel')),
      h('div', { class: 'modal-hero-text' },
        h('h3', {}, document.createTextNode(a.title)),
        h('div', { class: 'modal-hero-meta' },
          h('span', { class: 'num-chip' }, document.createTextNode(a.auctionNumber)),
          h('span', { class: badgeClass(a.status) }, a.status),
          a.isFeatured ? h('span', { class: 'badge warn' }, '★ مميز') : null)),
      h('button', {
        class: 'modal-x', type: 'button', 'aria-label': 'إغلاق', onclick: () => back.remove(),
      }, '×')),

    h('div', { class: 'modal-body' },
      readyPanel,

      section('التسعير', 'coins', 'gold',
        h('div', { class: 'stat-grid' },
          stat('السعر الأصلي', money(a.originalPrice, a.currency)),
          stat('سعر البداية', money(a.startingPrice, a.currency)),
          stat('السعر الحالي', money(a.currentPrice, a.currency)),
          stat(`السعر المستهدف (${a.targetPercentage}%)`, money(a.targetPrice, a.currency)),
          stat(`سعر المرحلة النهائية (${a.finalPhasePercentage}%)`, money(a.finalPhasePrice, a.currency)),
          stat('قيمة المزايدة', money(a.bidIncrement, a.currency)))),

      section('المشاركة والتوقيت', 'users', 'ok',
        h('div', { class: 'stat-grid' },
          stat('رسوم الاشتراك', money(a.entryFee, a.currency)),
          stat('مدة المزاد', `${a.durationSeconds} ثانية`),
          stat('مهلة البدء بعد اكتمال العدد', `${a.startDelayAfterParticipantsSeconds} ثانية`),
          stat('سياسة الانتهاء', a.expirationPolicy)),
        // Skipped when the ready panel is up, which already carries this same bar at 100%.
        canStart ? null : progressBar(a.participantCount, a.requiredParticipants, {
          label: 'المشاركون',
          text: `${a.participantCount} / ${a.requiredParticipants}`,
        })),

      section('مراحل المزاد', 'clock', 'info', timeline(a)),

      section(`الجوائز (${a.prizes.length})`, 'gift', 'warn',
        h('div', { class: 'table-wrap' },
          h('table', {},
            h('thead', {}, h('tr', {},
              h('th', {}, 'المركز'), h('th', {}, 'الصورة'), h('th', {}, 'المنتج'),
              h('th', { class: 'num' }, 'القيمة'))),
            h('tbody', {}, ...prizeRows))))),

    h('div', { class: 'modal-actions split' },
      // Reading: opens another dialog, changes nothing.
      h('div', { class: 'action-group' },
        h('button', { class: 'btn ghost sm', onclick: () => { back.remove(); openParticipants(a.id); } }, 'المشاركون'),
        ['RegistrationOpen', 'WaitingForParticipants'].includes(a.status)
          ? h('button', { class: 'btn ghost sm', onclick: () => { back.remove(); addParticipantDialog(a); } },
              icon('plus'), h('span', {}, 'إضافة مشارك'))
          : null,
        featureButton(a),
        h('button', { class: 'btn ghost sm', onclick: () => { back.remove(); openWinners(a.id); } }, 'الفائزون'),
        h('button', { class: 'btn ghost sm', onclick: () => { back.remove(); openEvents(a.id); } }, 'سجل الأحداث')),

      // Acting: each one writes to the auction and is recorded in the audit log.
      h('div', { class: 'action-group primary' },
        h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'إغلاق'),

        // Draft only. Once published nobody may quietly change what people signed up for, so the
        // button is gone from that point on. The server refuses the change in any case.
        a.status === 'Draft' ? h('button', {
          class: 'btn ghost',
          onclick: () => {
            back.remove();
            state.editAuctionId = a.id;
            state.cache.editAuction = null;
            state.route = 'auction-edit';
            render();
          },
        }, 'تعديل المزاد') : null,

        canStart ? h('button', {
          class: 'btn ghost', onclick: () => { back.remove(); editSchedule(a); },
        }, 'تعديل وقت البدء') : null,

        canPublish ? h('button', {
          class: 'btn', onclick: () => act(() => api(`/admin/auctions/${a.id}/publish`, { method: 'POST' }), 'Auction published.'),
        }, 'نشر المزاد') : null,

        canStart ? h('button', {
          class: 'btn ok', onclick: () => act(() => api(`/admin/auctions/${a.id}/start`, { method: 'POST' }), 'Auction started.'),
        }, 'ابدأ الآن') : null,

        canPause ? h('button', {
          class: 'btn ghost', onclick: () => act(() => api(`/admin/auctions/${a.id}/pause`, { method: 'POST' }), 'Auction paused.'),
        }, 'إيقاف مؤقت') : null,

        canResume ? h('button', {
          class: 'btn ok', onclick: () => act(() => api(`/admin/auctions/${a.id}/resume`, { method: 'POST' }), 'Auction resumed.'),
        }, 'استئناف') : null,

        canCancel ? h('button', {
          class: 'btn danger',
          onclick: () => {
            back.remove();
            confirmDialog('إلغاء المزاد',
              'سيتم إبلاغ المشاركين وإعادة رسوم الاشتراك بالكامل إلى المحفظة الرئيسية نقداً.',
              (reason) =>
              act(() => api(`/admin/auctions/${a.id}/cancel`, {
                method: 'POST', body: JSON.stringify({ reason }),
              }), 'Auction cancelled.'), { danger: true, needsReason: true });
          },
        }, 'إلغاء المزاد') : null))));

  document.body.append(back);
}

function editSchedule(a) {
  const current = a.scheduledStartAt ? new Date(a.scheduledStartAt) : new Date();
  const local = new Date(current.getTime() - current.getTimezoneOffset() * 60000).toISOString().slice(0, 16);

  // Written in Arabic here: a template literal builds a different string for every auction, so
  // the dictionary can never match it.
  formModal('Edit start time', `${a.auctionNumber} · الأوقات مخزّنة بالتوقيت العالمي`, [
    { name: 'scheduledStartAt', label: 'Scheduled start (local time)', type: 'datetime-local', value: local },
    { name: 'reason', label: 'Reason', placeholder: 'Recorded in the audit log' },
  ], async (v) => {
    await api(`/admin/auctions/${a.id}/schedule`, {
      method: 'PATCH',
      body: JSON.stringify({
        scheduledStartAt: new Date(v.scheduledStartAt).toISOString(),
        reason: v.reason,
      }),
    });
    toast('Start time updated. Participants were notified.', 'ok');
    render();
  });
}

/**
 * Arranges the app's featured row by dragging.
 *
 * The order is only meaningful as a whole, so nothing is saved until the administrator says so:
 * dragging rearranges the list on screen, and one request sends the final order. Sending a request
 * per drag would leave the row half-arranged whenever somebody closed the tab mid-thought.
 */
/**
 * The states an auction may be featured in, mirroring AuctionStateMachine.CanBeFeatured.
 *
 * The server decides; this list only keeps the console from offering a button that would be
 * refused. If the two ever drift, the server still wins and the administrator sees its message.
 */
const FEATURABLE = [
  'RegistrationOpen', 'WaitingForParticipants', 'Ready', 'WaitingForStart', 'Live', 'FinalPhase',
];

/** Puts an auction in the app's featured row, or takes it out. */
function featureButton(a) {
  const canFeature = FEATURABLE.includes(a.status);

  // Taking one out is always allowed, whatever state it has reached since.
  if (!a.isFeatured && !canFeature) {
    return h('button', {
      class: 'btn ghost sm',
      disabled: '',
      title: 'يمكن تمييز المزادات المفتوحة للاشتراك أو الجارية فقط. هذا المزاد ' + a.status + '.',
    }, '☆ تمييز المزاد');
  }

  return h('button', {
    class: a.isFeatured ? 'btn sm' : 'btn ghost sm',
    title: a.isFeatured
      ? 'يظهر هذا المزاد في صف «المزادات المميزة» أعلى الصفحة الرئيسية.'
      : 'المزادات المميزة تظهر في صف خاص أعلى الصفحة الرئيسية في التطبيق، ويخرج المزاد منه عند انتهائه.',
    onclick: () => act(
      () => api(`/admin/auctions/${a.id}/featured`, {
        method: 'PUT', body: JSON.stringify({ featured: !a.isFeatured }),
      }),
      a.isFeatured ? 'أُزيل المزاد من المزادات المميزة.' : 'أصبح المزاد ضمن المزادات المميزة.'),
  }, a.isFeatured ? '★ إلغاء التمييز' : '☆ تمييز المزاد');
}

async function openFeaturedOrder() {
  const data = await api('/admin/auctions?featured=true&pageSize=50');
  const back = h('div', { class: 'modal-back' });

  if (!data.items.length) {
    back.append(h('div', { class: 'modal' },
      h('h3', {}, 'ترتيب المزادات المميزة'),
      h('p', { class: 'sub' }, 'لا يوجد مزاد مميز بعد. ميّز مزاداً من قائمة المزادات أولاً.'),
      h('div', { class: 'modal-actions' },
        h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'إغلاق'))));

    document.body.append(back);
    return;
  }

  const list = h('div', { class: 'reorder' });
  let dragged = null;

  const rows = data.items.map((a) => {
    const row = h('div', { class: 'reorder-row', draggable: 'true', 'data-id': a.id },
      h('span', { class: 'reorder-grip', title: 'اسحب لتغيير الترتيب' }, '⠿'),
      h('span', { class: 'reorder-rank' }, ''),
      a.coverImageUrl || a.mainImageUrl
        ? h('img', { src: a.coverImageUrl || a.mainImageUrl, alt: '', class: 'cover-preview sm' })
        : h('div', { class: 'cover-preview sm empty' }, icon('box')),
      h('div', { class: 'reorder-text' },
        h('strong', {}, a.title || a.mainProductName),
        h('span', { class: 'mono sub' }, a.auctionNumber)),
      h('span', { class: badgeClass(a.status) }, a.status));

    row.addEventListener('dragstart', () => {
      dragged = row;
      // Deferred: changing the class in the same tick would make the browser drag a faded ghost.
      setTimeout(() => row.classList.add('dragging'), 0);
    });

    row.addEventListener('dragend', () => {
      row.classList.remove('dragging');
      dragged = null;
      renumber();
    });

    row.addEventListener('dragover', (e) => {
      e.preventDefault();
      if (!dragged || dragged === row) return;

      // Past the middle means the dragged row belongs after this one. Comparing against the
      // midpoint rather than the edge is what stops the list flickering between two positions
      // while the pointer sits on a boundary.
      const box = row.getBoundingClientRect();
      const after = e.clientY > box.top + box.height / 2;

      list.insertBefore(dragged, after ? row.nextSibling : row);
      renumber();
    });

    return row;
  });

  function renumber() {
    [...list.children].forEach((row, i) => {
      row.querySelector('.reorder-rank').textContent = String(i + 1);
    });
  }

  list.append(...rows);
  renumber();

  const save = h('button', { class: 'btn' }, 'حفظ الترتيب');

  save.onclick = async () => {
    const auctionIds = [...list.children].map((row) => row.dataset.id);

    save.disabled = true;
    try {
      await api('/admin/auctions/featured/order', {
        method: 'PUT',
        body: JSON.stringify({ auctionIds }),
      });

      toast('تم حفظ ترتيب المزادات المميزة.', 'ok');
      back.remove();
      render();
    } catch (err) {
      toast(err.message || 'تعذّر حفظ الترتيب.', 'bad');
      save.disabled = false;
    }
  };

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'ترتيب المزادات المميزة'),
    h('p', { class: 'sub' },
      'اسحب المزاد إلى مكانه. الأول في القائمة يظهر أولاً في صف «المزادات المميزة» بالتطبيق. '
      + 'المزاد المنتهي يخرج من الصف تلقائياً.'),
    list,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'إلغاء'),
      save)));

  document.body.append(back);
}

async function openParticipants(auctionId) {
  const data = await api(`/admin/auctions/${auctionId}/participants?pageSize=50`);
  const back = h('div', { class: 'modal-back' });

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'Participants'),
    h('p', { class: 'sub' }, 'Bid history is immutable and cannot be edited from here.'),
    h('div', { class: 'table-wrap' },
      h('table', {},
        h('thead', {}, h('tr', {},
          h('th', {}, 'Rank'), h('th', {}, 'Participant'), h('th', {}, 'Joined'),
          h('th', { class: 'num' }, 'Entry fee'), h('th', { class: 'num' }, 'Highest'),
          h('th', { class: 'num' }, 'Bids'), h('th', {}, 'Status'))),
        h('tbody', {}, ...data.items.map((p) => h('tr', {},
          h('td', {}, p.currentRank || '—'),
          h('td', {}, p.fullName || shortId(p.userId)),
          h('td', {}, when(p.joinedAt)),
          h('td', { class: 'num' }, money(p.entryFeePaid, p.currency)),
          h('td', { class: 'num' }, money(p.highestPrice, p.currency)),
          h('td', { class: 'num' }, p.bidCount),
          h('td', {}, h('span', { class: badgeClass(p.status) }, p.status))))))),
    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

/*
 * Add a user to an auction, at an entry fee the administrator sets.
 *
 * The fee comes out of the user's Main wallet, the same as when they join from the app, and the
 * server refuses the request if that balance cannot cover it. The balance shown here is only a
 * guide for the administrator: the server re-checks it inside the same transaction that charges it.
 */
function addParticipantDialog(a) {
  const back = h('div', { class: 'modal-back' });
  const error = h('div', { class: 'err-text' });

  const search = h('input', { placeholder: 'ابحث بالاسم أو البريد الإلكتروني أو رقم الهاتف' });
  const results = h('div', { class: 'pick-list' });
  const fee = h('input', { type: 'number', step: '0.01', min: '0', value: String(a.entryFee) });
  const chosenBox = h('div', { class: 'hint' }, 'لم يُختر مشارك بعد.');
  const saveBtn = h('button', { class: 'btn', disabled: '' }, 'إضافة وخصم الرسوم');

  let chosen = null;

  const refresh = () => {
    const value = Number(fee.value);
    const valid = fee.value !== '' && value >= 0;
    const enough = chosen && value <= Number(chosen.mainWalletBalance);

    chosenBox.replaceChildren();
    if (chosen) {
      chosenBox.append(
        h('strong', {}, document.createTextNode(chosen.fullName || chosen.email)),
        document.createTextNode(` · رصيد المحفظة الرئيسية ${moneyText(chosen.mainWalletBalance, a.currency)}`));
      if (valid && !enough) {
        chosenBox.append(h('div', { class: 'err-text' },
          'رصيد المحفظة الرئيسية لهذا المشارك لا يكفي لقيمة الاشتراك.'));
      }
    } else {
      chosenBox.textContent = 'لم يُختر مشارك بعد.';
    }

    saveBtn.disabled = chosen && valid && enough ? null : '';
  };

  let timer = null;
  const runSearch = async () => {
    const term = search.value.trim();
    if (term.length < 2) { results.replaceChildren(); return; }

    try {
      const page = await api(`/admin/users?search=${encodeURIComponent(term)}&pageSize=8&isActive=true`);
      results.replaceChildren(...(page.items.length ? page.items.map((u) => {
        // Names and addresses are user data, so they go in as text, never as markup.
        const row = h('button', { type: 'button', class: chosen?.id === u.id ? 'pick on' : 'pick' },
          h('span', { class: 'pick-name' }, document.createTextNode(u.fullName || '—')),
          h('span', { class: 'pick-sub' }, document.createTextNode(u.email || u.phoneNumber || '')),
          h('span', { class: 'pick-bal' }, moneyText(u.mainWalletBalance, a.currency)));
        row.addEventListener('click', () => { chosen = u; runSearch(); refresh(); });
        return row;
      }) : [h('div', { class: 'empty' }, 'لا يوجد مستخدم مطابق.')]));
    } catch (e) {
      results.replaceChildren(h('div', { class: 'err-text' }, e.message));
    }
  };

  search.addEventListener('input', () => { clearTimeout(timer); timer = setTimeout(runSearch, 250); });
  fee.addEventListener('input', refresh);

  const messages = {
    INSUFFICIENT_BALANCE: 'رصيد المحفظة الرئيسية لهذا المشارك لا يكفي لقيمة الاشتراك.',
    ALREADY_JOINED: 'هذا المستخدم مشترك في المزاد بالفعل.',
    AUCTION_FULL: 'اكتمل عدد المشاركين في هذا المزاد.',
    REGISTRATION_CLOSED: 'التسجيل في هذا المزاد مغلق.',
    ACCOUNT_DISABLED: 'هذا الحساب موقوف.',
  };

  saveBtn.addEventListener('click', async () => {
    error.textContent = '';
    saveBtn.disabled = '';
    try {
      await api(`/admin/auctions/${a.id}/participants`, {
        method: 'POST',
        body: JSON.stringify({ userId: chosen.id, entryFee: Number(fee.value) }),
      });
      toast(`تمت إضافة ${chosen.fullName || chosen.email} وخُصمت ${moneyText(Number(fee.value), a.currency)} من محفظته الرئيسية.`, 'ok');
      back.remove();
      render();
    } catch (e) {
      error.textContent = messages[e.code] || e.message;
      refresh();
    }
  });

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'إضافة مشارك'),
    h('p', { class: 'sub' },
      document.createTextNode(`${a.auctionNumber} · تُخصم قيمة الاشتراك من المحفظة الرئيسية للمشارك، ولا تُقبل الإضافة إن لم يكفِ رصيدها.`)),
    h('label', {}, 'المشارك'), search, results, chosenBox,
    h('label', {}, `قيمة الاشتراك (${a.currency})`), fee,
    h('div', { class: 'hint' }, `رسوم الاشتراك في هذا المزاد ${moneyText(a.entryFee, a.currency)}، ويمكنك تغييرها لهذا المشارك وحده.`),
    error,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'إلغاء'),
      saveBtn)));

  document.body.append(back);
  search.focus();
}

async function openWinners(auctionId) {
  const winners = await api(`/admin/auctions/${auctionId}/winners`);
  const back = h('div', { class: 'modal-back' });

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'Winners'),
    h('p', { class: 'sub' }, 'Ranked by highest price, then by the earliest server timestamp at which it was reached.'),
    winners.length
      ? h('div', { class: 'table-wrap' }, h('table', {},
          h('thead', {}, h('tr', {}, h('th', {}, 'Rank'), h('th', {}, 'Winner'),
            h('th', { class: 'num' }, 'Highest'), h('th', {}, 'Prize'),
            h('th', { class: 'num' }, 'Prize value'), h('th', {}, 'Claim'))),
          h('tbody', {}, ...winners.map((w) => h('tr', {},
            h('td', {}, `#${w.rank}`),
            h('td', {}, w.displayName),
            h('td', { class: 'num' }, money(w.highestPrice, w.currency)),
            h('td', {}, w.prizeName),
            h('td', { class: 'num' }, money(w.prizeValue, w.currency)),
            h('td', {}, h('span', { class: badgeClass(w.claimStatus) }, w.claimStatus || '—')))))))
      : h('div', { class: 'empty' }, 'Winners have not been declared yet.'),
    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

async function openEvents(auctionId) {
  const data = await api(`/admin/auctions/${auctionId}/events?pageSize=100`);
  const back = h('div', { class: 'modal-back' });

  back.append(h('div', { class: 'modal' },
    h('h3', {}, 'Auction event log'),
    h('p', { class: 'sub' }, 'Append-only record of everything that happened to this auction.'),
    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, 'When'), h('th', {}, 'Event'), h('th', {}, 'From'), h('th', {}, 'To'))),
      h('tbody', {}, ...data.items.map((e) => h('tr', {},
        h('td', {}, when(e.timestamp)),
        h('td', {}, h('span', { class: 'badge' }, e.eventType)),
        h('td', { class: 'mono' }, e.oldValue || '—'),
        h('td', { class: 'mono' }, e.newValue || '—')))))),
    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

// ---------------------------------------------------------------- create auction

/**
 * Sends one picked file to the API and returns the address it will be served at.
 *
 * Goes through `api` rather than calling fetch directly. An access token lasts fifteen minutes,
 * which is easily less than the time it takes to fill this form in, and `api` is where a stale
 * token is refreshed and the request retried. Uploading on its own returned 401 to an
 * administrator who was still signed in and still working.
 */
async function uploadImage(file) {
  let upload = file;

  // A phone photo is easily over the server's 5 MB limit, and HEIC is not something the server
  // accepts at all. Either is redrawn here as a JPEG of sensible size, which is all a prize or
  // product picture needs. Anything already small and in a supported format goes up untouched.
  if (file.size > UPLOAD_LIMIT || !UPLOAD_TYPES.test(file.type)) {
    upload = await shrinkImage(file);
  }
  if (upload.size > UPLOAD_LIMIT) throw new Error('حجم الصورة أكبر من 5 ميغابايت. اختر صورة أصغر.');

  const form = new FormData();
  form.append('file', upload, upload.name || file.name);

  try {
    const body = await api('/admin/uploads/image', { method: 'POST', body: form });
    return body.url;
  } catch (e) {
    if (/Only JPEG/.test(e.message)) throw new Error('صيغة الصورة غير مدعومة. استخدم JPG أو PNG أو WebP.');
    if (/too large|larger than/i.test(e.message)) throw new Error('حجم الصورة أكبر من 5 ميغابايت. اختر صورة أصغر.');
    throw new Error(`تعذّر رفع الصورة: ${e.message}`);
  }
}

const UPLOAD_LIMIT = 5 * 1024 * 1024;
const UPLOAD_TYPES = /^image\/(jpeg|png|webp|gif)$/;

/**
 * Redraws a picture as a JPEG no wider or taller than 2000 px, stepping down until it fits the
 * upload limit. The background is painted white first, so a transparent PNG does not turn black.
 */
async function shrinkImage(file) {
  let bitmap;
  try {
    bitmap = await createImageBitmap(file);
  } catch {
    throw new Error('تعذّر قراءة الصورة. استخدم صورة بصيغة JPG أو PNG أو WebP.');
  }

  for (const [side, quality] of [[2000, 0.85], [1600, 0.8], [1200, 0.75]]) {
    const scale = Math.min(1, side / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(bitmap.width * scale);
    canvas.height = Math.round(bitmap.height * scale);

    const ctx = canvas.getContext('2d');
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);

    const blob = await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', quality));
    if (blob && blob.size <= UPLOAD_LIMIT) {
      bitmap.close();
      return new File([blob], file.name.replace(/\.\w+$/, '') + '.jpg', { type: 'image/jpeg' });
    }
  }

  bitmap.close();
  throw new Error('حجم الصورة أكبر من 5 ميغابايت. اختر صورة أصغر.');
}

/**
 * Add or edit one prize.
 *
 * The picture is chosen from the administrator's machine and uploaded as soon as it is picked, so
 * the dialog holds an address rather than a file and saving the prize cannot fail halfway.
 */
function prizeDialog({ rank, prize, takenRanks, onSave }) {
  const back = h('div', { class: 'modal-back' });
  const error = h('div', { class: 'err-text' });

  const name = h('input', { value: prize?.name || '', placeholder: 'مثال: سيارة دفع رباعي فاخرة' });
  const price = h('input', { type: 'number', step: '0.01', value: prize?.price ?? '', placeholder: '0.00' });
  const note = h('input', { value: prize?.description || '', placeholder: 'وصف قصير (اختياري)' });

  const rankOptions = [1, 2, 3, 4].map((r) => h('option', {
    value: String(r),
    selected: r === (prize?.rank ?? rank) ? '' : null,
    disabled: takenRanks.includes(r) && r !== prize?.rank ? '' : null,
  }, `المركز ${r}`));
  const rankSelect = h('select', {}, ...rankOptions);

  let imageUrl = prize?.imageUrl || null;

  const thumb = h('div', { class: imageUrl ? 'prize-thumb' : 'prize-thumb empty' },
    imageUrl ? h('img', { src: imageUrl, alt: '' }) : icon('box'));

  const fileInput = h('input', { type: 'file', accept: 'image/*' });
  const pickLabel = h('button', { class: 'btn ghost sm', type: 'button' }, 'اختر صورة');
  const removeBtn = h('button', { class: 'btn ghost sm', type: 'button' }, 'إزالة الصورة');
  const fileState = h('div', { class: 'hint' }, 'JPEG أو PNG أو GIF · حتى ٥ ميجابايت');

  const paintThumb = () => {
    thumb.replaceChildren();
    if (imageUrl) {
      thumb.className = 'prize-thumb';
      thumb.append(h('img', { src: imageUrl, alt: '' }));
    } else {
      thumb.className = 'prize-thumb empty';
      thumb.append(icon('box'));
    }
    removeBtn.disabled = imageUrl ? null : '';
  };

  pickLabel.addEventListener('click', () => fileInput.click());
  removeBtn.addEventListener('click', () => { imageUrl = null; paintThumb(); fileState.textContent = 'لا توجد صورة'; });

  fileInput.addEventListener('change', async () => {
    const file = fileInput.files?.[0];
    if (!file) return;

    error.textContent = '';
    pickLabel.disabled = '';
    fileState.textContent = 'جارٍ الرفع…';

    try {
      imageUrl = await uploadImage(file);
      paintThumb();
      fileState.textContent = 'تم رفع الصورة';
    } catch (e) {
      fileState.textContent = '';
      error.textContent = e.message;
    } finally {
      pickLabel.disabled = null;
      // Clearing lets the same file be picked again after a failure.
      fileInput.value = '';
    }
  });

  const save = () => {
    const value = Number(price.value || 0);

    if (!name.value.trim()) { error.textContent = 'اسم الجائزة مطلوب.'; return; }
    if (!(value > 0)) { error.textContent = 'قيمة الجائزة يجب أن تكون أكبر من صفر.'; return; }

    onSave({
      rank: Number(rankSelect.value),
      name: name.value.trim(),
      price: value,
      imageUrl,
      description: note.value.trim() || null,
    });
    back.remove();
  };

  paintThumb();

  back.append(h('div', { class: 'modal' },
    h('h3', {}, prize ? 'تعديل الجائزة' : 'إضافة جائزة'),
    h('p', { class: 'sub' }, 'قيمة جائزة المركز الأول هي قيمة المزاد.'),

    h('div', { class: 'prize-dialog' },
      h('div', {}, thumb, h('div', { class: 'row', style: 'margin-top:10px' }, pickLabel, removeBtn), fileState),
      h('div', {},
        h('label', {}, 'المركز'), rankSelect,
        h('label', {}, 'اسم الجائزة'), name,
        h('label', {}, 'القيمة'), price,
        h('label', {}, 'ملاحظة'), note)),

    fileInput,
    error,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Cancel'),
      h('button', { class: 'btn', onclick: save }, 'Save'))));

  fileInput.hidden = true;
  document.body.append(back);
  name.focus();
}

/*
 * Create an auction, or correct one that is still a draft.
 *
 * One form does both. A draft has never been seen by a participant and holds no money, so every
 * part of it can still be changed; the moment it is published that stops, because people would be
 * signing up for one thing and getting another. `state.editAuctionId` is what puts this form in
 * edit mode, and the server enforces the same limit regardless of what the console shows.
 *
 * Prizes are entered here, against this auction: a name, a value, a picture and an optional note
 * for each of the four places. They are not picked from a shared catalog, so two auctions can
 * award the same thing and still keep their own wording and picture for it, and editing one can
 * never change what another awards.
 *
 * There is no "original price" field. The auction is worth what its first prize is worth, so that
 * value is read from the first-place prize and shown back read-only. A typed field allowed an
 * auction to advertise one value while awarding another, and nothing checked the two agreed.
 */
async function viewAuctionForm() {
  const f = {};

  const editId = state.editAuctionId || null;
  const existing = editId ? await api(`/admin/auctions/${editId}`) : null;

  /*
   * The prizes being worked on.
   *
   * Seeded from the saved auction in edit mode, but only once per auction: the prize table is
   * repainted in place rather than through render(), and re-seeding on every repaint would undo
   * whatever had just been changed.
   */
  const cache = editId
    ? (state.cache.editAuction && state.cache.editAuction.id === editId
        ? state.cache.editAuction
        : (state.cache.editAuction = {
            id: editId,
            prizes: existing.prizes.map((p) => ({
              rank: p.rank,
              name: p.productName || '',
              price: Number(p.originalPrice),
              imageUrl: p.imageUrl || null,
              description: p.description || null,
            })),
          }))
    : (state.cache.newAuction || (state.cache.newAuction = { prizes: [] }));

  /** The saved value when editing, the sensible default when creating. */
  const start = (key, fallback) =>
    existing && existing[key] !== null && existing[key] !== undefined
      ? String(existing[key])
      : fallback;

  /** Leaves the form, dropping whatever was being worked on. */
  const leave = () => {
    if (editId) {
      state.cache.editAuction = null;
      state.editAuctionId = null;
    } else {
      state.cache.newAuction = { prizes: [] };
    }
    state.route = 'auctions';
    render();
  };

  const field = (name, label, attrs = {}, hint) => {
    const input = h('input', attrs);
    f[name] = input;
    return h('div', { class: 'field' },
      h('label', {}, label), input, hint ? h('div', { class: 'hint' }, hint) : null);
  };

  const select = (name, label, options, info = null, value = null) => {
    const sel = h('select', {}, ...options.map((o) =>
      h('option', { value: o.value, selected: o.value === value ? '' : null }, o.label)));
    f[name] = sel;
    return h('div', { class: 'field' },
      h('div', { class: 'label-row' }, h('label', {}, label), info),
      sel);
  };

  const error = h('div', { class: 'err-text' });
  const preview = h('div', { class: 'hint' });
  const originalOut = h('div', { class: 'derived-value' }, '—');
  const prizeBody = h('div', {});

  /*
   * The auction's own picture, separate from the prize pictures. It is what the app shows on the
   * auction's card and at the top of its page; without one, the first prize's picture stands in.
   * Kept on the cache like the prizes, so a repaint does not lose a picture just uploaded.
   */
  if (cache.coverImageUrl === undefined) cache.coverImageUrl = existing?.coverImageUrl || null;

  const coverThumb = h('div', {});
  const coverState = h('div', { class: 'hint' }, 'JPEG أو PNG أو GIF · حتى ٥ ميجابايت. يُفضّل صورة عرضية.');
  const coverInput = h('input', { type: 'file', accept: 'image/*' });
  coverInput.hidden = true;
  const coverPick = h('button', { class: 'btn ghost sm', type: 'button' }, 'اختر صورة');
  const coverRemove = h('button', { class: 'btn ghost sm', type: 'button' }, 'إزالة الصورة');

  const paintCover = () => {
    coverThumb.replaceChildren(cache.coverImageUrl
      ? h('img', { src: cache.coverImageUrl, alt: '', class: 'cover-preview' })
      : h('div', { class: 'cover-preview empty' }, icon('box'), h('span', {}, 'صورة الجائزة الأولى ستظهر بدلاً منها')));
    coverRemove.disabled = cache.coverImageUrl ? null : '';
  };

  coverPick.addEventListener('click', () => coverInput.click());
  coverRemove.addEventListener('click', () => { cache.coverImageUrl = null; paintCover(); });
  coverInput.addEventListener('change', async () => {
    const file = coverInput.files?.[0];
    if (!file) return;

    coverPick.disabled = '';
    coverState.textContent = 'جارٍ الرفع…';
    try {
      cache.coverImageUrl = await uploadImage(file);
      paintCover();
      coverState.textContent = 'تم رفع الصورة';
    } catch (e) {
      coverState.textContent = e.message;
    } finally {
      coverPick.disabled = null;
      // Clearing lets the same file be picked again after a failure.
      coverInput.value = '';
    }
  });
  paintCover();

  const sorted = () => [...cache.prizes].sort((a, b) => a.rank - b.rank);
  const mainPrize = () => cache.prizes.find((p) => p.rank === 1) || null;

  const recompute = () => {
    const original = Number(mainPrize()?.price || 0);
    const target = original * Number(f.targetPercentage?.value || 0) / 100;
    const finalPhase = original * Number(f.finalPhasePercentage?.value || 0) / 100;

    originalOut.textContent = original > 0 ? moneyText(original, 'SAR') : '—';
    preview.textContent = original > 0
      ? `السعر المستهدف ${moneyText(target, 'SAR')} · تبدأ المرحلة النهائية عند ${moneyText(finalPhase, 'SAR')}`
        + ' · يجب أن يكون سعر البداية أقل من السعر المستهدف.'
      : 'أضف جائزة المركز الأول أولاً؛ من قيمتها تُحسب باقي الأسعار.';
  };

  const paintPrizes = () => {
    const rows = sorted();

    if (!rows.length) {
      prizeBody.replaceChildren(h('div', { class: 'empty' }, 'لم تُضف أي جائزة بعد.'));
      recompute();
      hero.mark();
      return;
    }

    const table = h('table', {},
      h('thead', {}, h('tr', {},
        h('th', {}, 'المركز'), h('th', {}, 'الصورة'), h('th', {}, 'اسم الجائزة'),
        h('th', { class: 'num' }, 'القيمة'), h('th', {}, ''))),
      h('tbody', {}, ...rows.map((prize) => {
        // Names and notes are data, so they are appended raw and never translated.
        const nameCell = h('td', {});
        nameCell.append(document.createTextNode(prize.name));
        if (prize.description) {
          nameCell.append(h('div', { class: 'hint' }, document.createTextNode(prize.description)));
        }

        return h('tr', {},
          h('td', {}, prize.rank === 1
            ? h('span', { class: 'badge info' }, 'المركز ١')
            : `المركز ${prize.rank}`),
          h('td', {}, prizeThumb(prize.imageUrl)),
          nameCell,
          h('td', { class: 'num' }, money(prize.price, 'SAR')),
          h('td', {}, h('div', { class: 'row' },
            h('button', {
              class: 'btn ghost sm',
              onclick: () => prizeDialog({
                prize,
                takenRanks: cache.prizes.map((p) => p.rank),
                onSave: (saved) => {
                  cache.prizes = cache.prizes.filter((p) => p !== prize && p.rank !== saved.rank);
                  cache.prizes.push(saved);
                  paintPrizes();
                },
              }),
            }, 'Edit'),
            h('button', {
              class: 'btn danger sm',
              onclick: () => {
                cache.prizes = cache.prizes.filter((p) => p !== prize);
                paintPrizes();
              },
            }, 'Remove'))));
      })));

    prizeBody.replaceChildren(h('div', { class: 'table-wrap' }, table));
    recompute();
    hero.mark();
  };

  const addPrize = () => {
    const taken = cache.prizes.map((p) => p.rank);
    const nextRank = [1, 2, 3, 4].find((r) => !taken.includes(r));

    if (!nextRank) {
      toast('المزاد يحتوي على أربع جوائز بالفعل.', 'err');
      return;
    }

    prizeDialog({
      rank: nextRank,
      takenRanks: taken,
      onSave: (saved) => {
        cache.prizes = cache.prizes.filter((p) => p.rank !== saved.rank);
        cache.prizes.push(saved);
        paintPrizes();
      },
    });
  };

  const submit = async () => {
    error.textContent = '';

    if (cache.prizes.length !== 4) {
      error.textContent = 'المزاد يحتاج إلى أربع جوائز بالضبط.';
      return;
    }

    const body = {
      title: f.title.value.trim(),
      description: f.description.value.trim() || null,
      startingPrice: Number(f.startingPrice.value),
      targetPercentage: Number(f.targetPercentage.value),
      finalPhasePercentage: Number(f.finalPhasePercentage.value),
      bidIncrement: Number(f.bidIncrement.value),
      entryFee: Number(f.entryFee.value),
      requiredParticipants: Number(f.requiredParticipants.value),
      startDelayAfterParticipantsSeconds: Number(f.startDelay.value),
      durationSeconds: Number(f.duration.value),
      expirationPolicy: f.expirationPolicy.value,
      prizes: sorted(),
      // An empty string on an edit is what removes a picture that was there before.
      coverImageUrl: cache.coverImageUrl || (editId ? '' : null),
    };

    try {
      if (editId) {
        // Every field is sent, including the prizes, so removing one actually removes it.
        const saved = await api(`/admin/auctions/${editId}`, {
          method: 'PUT', body: JSON.stringify(body),
        });
        toast(`تم حفظ التعديلات على ${saved.auctionNumber}.`, 'ok');
        state.cache.editAuction = null;
        state.editAuctionId = null;
      } else {
        // The currency is fixed at creation and cannot be changed afterwards.
        const created = await api('/admin/auctions', {
          method: 'POST', body: JSON.stringify({ ...body, currency: 'SAR' }),
        });
        toast(`Created ${created.auctionNumber}. Publish it when the prizes are correct.`, 'ok');

        // A fresh form next time; the draft is saved on the server now.
        state.cache.newAuction = { prizes: [] };
      }

      state.route = 'auctions';
      render();
    } catch (e) {
      error.textContent = e.message + (e.details?.length ? ` — ${e.details.join('; ')}` : '');
    }
  };

  const hero = formHero({
    title: editId ? 'تعديل المزاد' : 'إنشاء مزاد جديد',
    subtitle: editId
      ? `${existing.auctionNumber} · مسودة لم تُنشر بعد، فيمكن تعديل كل شيء فيها.`
      : 'يُحفظ المزاد كمسودة. لن يراه أحد قبل أن تنشره.',
    action: h('button', { class: 'btn ghost', onclick: leave }, 'Cancel'),
    steps: [
      {
        number: '١',
        label: 'المعلومات الأساسية',
        state: () => ({ done: !!f.title?.value.trim() }),
      },
      {
        number: '٢',
        label: 'الجوائز',
        state: () => ({
          done: cache.prizes.length === 4,
          // Latin digits, matching every other figure on the page.
          note: `${cache.prizes.length} من 4`,
        }),
      },
      {
        number: '٣',
        label: 'التسعير',
        state: () => {
          const original = Number(mainPrize()?.price || 0);
          return { done: original > 0, note: original > 0 ? moneyText(original, 'SAR') : 'بانتظار الجائزة الأولى' };
        },
      },
      {
        number: '٤',
        label: 'المشاركة والتوقيت',
        state: () => ({
          done: Number(f.requiredParticipants?.value || 0) > 0,
          note: f.requiredParticipants?.value ? `${f.requiredParticipants.value} مشارك` : '',
        }),
      },
    ],
  });

  const view = h('div', {},
    hero.el,

    h('div', { class: 'card' },
      sectionHead('1 · Basic information', 'gavel', 'info'),
      field('title', 'Title', {
        placeholder: 'مثال: مزاد السيارة الفاخرة الكبير', value: start('title', ''),
      }),
      field('description', 'Description', {
        placeholder: 'Optional', value: start('description', ''),
      }),
      h('div', { class: 'field' },
        h('label', {}, 'صورة المزاد'),
        coverThumb,
        h('div', { class: 'row', style: 'margin-top:10px' }, coverPick, coverRemove),
        coverState,
        coverInput)),

    h('div', { class: 'card' },
      sectionHead('2 · Prizes', 'gift', 'warn'),
      h('div', { class: 'toolbar' },
        h('button', { class: 'btn', onclick: addPrize }, icon('plus'), h('span', {}, 'إضافة جائزة')),
        h('span', { class: 'hint' }, 'الجوائز تخص هذا المزاد وحده. قيمة المركز الأول هي قيمة المزاد.')),
      prizeBody),

    h('div', { class: 'card' },
      sectionHead('3 · Pricing', 'coins', 'ok'),
      h('div', { class: 'derived' },
        h('label', {}, 'السعر الأصلي'),
        originalOut,
        h('div', { class: 'hint' }, 'مأخوذ من قيمة جائزة المركز الأول.')),
      h('div', { class: 'grid-2' },
        field('startingPrice', 'Starting price',
          { type: 'number', value: start('startingPrice', '1000'), step: '0.01' }),
        field('bidIncrement', 'Bid increment',
          { type: 'number', value: start('bidIncrement', '20'), step: '0.01' },
          'Moves the price; it is not charged to the bidder.')),
      h('div', { class: 'grid-2' },
        field('targetPercentage', 'Target %',
          { type: 'number', value: start('targetPercentage', '50'), step: '0.01' }),
        field('finalPhasePercentage', 'Final phase %',
          { type: 'number', value: start('finalPhasePercentage', '40'), step: '0.01' })),
      preview),

    h('div', { class: 'card' },
      sectionHead('4 · Participation & timing', 'users', 'violet'),
      h('div', { class: 'grid-3' },
        field('entryFee', 'Entry fee',
          { type: 'number', value: start('entryFee', '20'), step: '0.01' },
          'Charged once from the Main wallet.'),
        field('requiredParticipants', 'Required participants',
          { type: 'number', value: start('requiredParticipants', '400') }),
        field('duration', 'Duration (seconds)',
          { type: 'number', value: start('durationSeconds', '180') })),
      h('div', { class: 'grid-2' },
        field('startDelay', 'Start delay after target reached (seconds)',
          { type: 'number', value: start('startDelayAfterParticipantsSeconds', '7200') }),
        select('expirationPolicy', 'Expiration policy', [
          { value: 'EndAndDistribute', label: 'End and distribute (recommended)' },
          { value: 'Extend', label: 'Extend' },
          { value: 'Cancel', label: 'Cancel' },
          { value: 'Restart', label: 'Restart' },
        ], infoHint('ماذا يحدث إذا انتهى الوقت دون بلوغ السعر المستهدف؟', [
          ['إنهاء وتوزيع الجوائز',
            'ينتهي المزاد. تُرتَّب المشاركات حسب أعلى سعر، فيفوز أصحاب المراكز الأربعة الأولى '
            + 'بالجوائز، ويستعيد الباقون رسوم الاشتراك كرصيد تسوق. هذا هو الخيار الموصى به، '
            + 'لأنه الوحيد الذي ينهي المزاد ويوزّع الجوائز من تلقاء نفسه.'],
          ['تمديد المدة',
            'لا ينتهي المزاد. تُضاف ٦٠ ثانية ويستمر، ويتكرر ذلك كلما انتهى الوقت، حتى يبلغ '
            + 'أحد المشاركين السعر المستهدف.'],
          ['إلغاء',
            'يُلغى المزاد. لا يفوز أحد ولا تُوزَّع جوائز، وتعود رسوم الاشتراك كاملةً إلى '
            + 'المحفظة الرئيسية نقداً لكل من دفعها.'],
          ['إعادة التشغيل',
            'يعود السعر إلى سعر البداية ويبدأ المزاد من جديد. يبقى المشاركون كما هم ويبقى سجل '
            + 'المزايدات، لكن السعر يُصفَّر.'],
        ]), start('expirationPolicy', 'EndAndDistribute')))),

    h('div', { class: 'card' },
      error,
      h('div', { class: 'row' },
        h('button', { class: 'btn', onclick: submit },
          editId ? 'حفظ التعديلات' : 'Create as draft'),
        h('button', { class: 'btn ghost', onclick: leave }, 'Cancel'))));

  ['targetPercentage', 'finalPhasePercentage'].forEach((k) =>
    f[k].addEventListener('input', recompute));

  ['title', 'requiredParticipants'].forEach((k) =>
    f[k].addEventListener('input', hero.mark));

  paintPrizes();
  return view;
}

// ---------------------------------------------------------------- website

/*
 * The public landing page's content.
 *
 * Everything the page says lives in the database, so these four screens are the whole front page:
 * its wording here, its cards, its reviews and its questions on the pages below. Nothing about
 * the site is edited by changing code.
 */

/** A labelled text box that reports itself back through `f`. */
function siteField(f, name, label, value, opts = {}) {
  const el = opts.long
    ? h('textarea', { rows: opts.rows || 3 }, value || '')
    : h('input', { type: opts.type || 'text', value: value ?? '', placeholder: opts.placeholder || '' });

  f[name] = el;

  return h('div', { class: 'field' },
    h('label', {}, label),
    el,
    opts.hint ? h('div', { class: 'hint' }, opts.hint) : null);
}

/** A shown/hidden switch for a whole section of the page. */
function siteToggle(f, name, label, value, hint) {
  const sel = h('select', {},
    h('option', { value: 'true', selected: value ? '' : null }, 'ظاهر في الصفحة'),
    h('option', { value: 'false', selected: value ? null : '' }, 'مخفي'));

  f[name] = sel;

  return h('div', { class: 'field' },
    h('label', {}, label),
    sel,
    hint ? h('div', { class: 'hint' }, hint) : null);
}

const siteText = (el) => (el.value || '').trim() || null;
const siteBool = (el) => el.value === 'true';

/** An empty box means "publish the real count", so it must survive as null rather than 0. */
const siteCount = (el) => {
  const raw = (el.value || '').trim();
  return raw === '' ? null : Math.max(0, Number(raw));
};

async function viewSiteContent() {
  const data = await api('/admin/site/content');
  const s = data.settings;
  const stats = data.stats;

  const f = {};
  const error = h('div', { class: 'err-text' });

  const save = async () => {
    error.textContent = '';
    try {
      await api('/admin/site/settings', {
        method: 'PUT',
        body: JSON.stringify({
          heroBadge: siteText(f.heroBadge),
          heroTitle: siteText(f.heroTitle) || '',
          heroSubtitle: siteText(f.heroSubtitle),
          heroCtaLabel: siteText(f.heroCtaLabel),
          heroCtaUrl: siteText(f.heroCtaUrl),
          heroImageUrl: siteText(f.heroImageUrl),

          appStoreUrl: siteText(f.appStoreUrl),
          googlePlayUrl: siteText(f.googlePlayUrl),

          aboutEnabled: siteBool(f.aboutEnabled),
          aboutEyebrow: siteText(f.aboutEyebrow),
          aboutTitle: siteText(f.aboutTitle),
          featuresTitle: siteText(f.featuresTitle),
          featuresBody: siteText(f.featuresBody),
          visionTitle: siteText(f.visionTitle),
          visionBody: siteText(f.visionBody),

          statsEnabled: siteBool(f.statsEnabled),
          statUsersOverride: siteCount(f.statUsersOverride),
          statAuctionsOverride: siteCount(f.statAuctionsOverride),
          statProductsOverride: siteCount(f.statProductsOverride),
          statUsersLabel: siteText(f.statUsersLabel),
          statAuctionsLabel: siteText(f.statAuctionsLabel),
          statProductsLabel: siteText(f.statProductsLabel),

          stepsEnabled: siteBool(f.stepsEnabled),
          stepsEyebrow: siteText(f.stepsEyebrow),
          stepsTitle: siteText(f.stepsTitle),
          stepsSubtitle: siteText(f.stepsSubtitle),

          participateEnabled: siteBool(f.participateEnabled),
          participateEyebrow: siteText(f.participateEyebrow),
          participateTitle: siteText(f.participateTitle),
          participateSubtitle: siteText(f.participateSubtitle),

          showcaseEnabled: siteBool(f.showcaseEnabled),
          showcaseEyebrow: siteText(f.showcaseEyebrow),
          showcaseTitle: siteText(f.showcaseTitle),
          showcaseSubtitle: siteText(f.showcaseSubtitle),

          testimonialsEnabled: siteBool(f.testimonialsEnabled),
          testimonialsEyebrow: siteText(f.testimonialsEyebrow),
          testimonialsTitle: siteText(f.testimonialsTitle),
          testimonialsSubtitle: siteText(f.testimonialsSubtitle),

          faqEnabled: siteBool(f.faqEnabled),
          faqEyebrow: siteText(f.faqEyebrow),
          faqTitle: siteText(f.faqTitle),
          faqSubtitle: siteText(f.faqSubtitle),

          downloadEnabled: siteBool(f.downloadEnabled),
          downloadTitle: siteText(f.downloadTitle),
          downloadSubtitle: siteText(f.downloadSubtitle),

          contactEmail: siteText(f.contactEmail),
          contactPhone: siteText(f.contactPhone),
          footerNote: siteText(f.footerNote),
          privacyUrl: siteText(f.privacyUrl),
          termsUrl: siteText(f.termsUrl),
          twitterUrl: siteText(f.twitterUrl),
          instagramUrl: siteText(f.instagramUrl),
          tiktokUrl: siteText(f.tiktokUrl),
          snapchatUrl: siteText(f.snapchatUrl),
          whatsappUrl: siteText(f.whatsappUrl),

          metaTitle: siteText(f.metaTitle),
          metaDescription: siteText(f.metaDescription),
        }),
      });
      toast('تم حفظ محتوى الصفحة.', 'ok');
    } catch (e) {
      error.textContent = e.message + (e.details?.length ? ` — ${e.details.join('; ')}` : '');
    }
  };

  // A picture is uploaded from the machine, the same way a prize picture is.
  const heroPicker = (() => {
    const input = h('input', { type: 'file', accept: 'image/*' });
    input.hidden = true;

    const state = h('span', { class: 'hint' });

    input.addEventListener('change', async () => {
      const file = input.files?.[0];
      if (!file) return;

      state.textContent = 'جارٍ الرفع…';
      try {
        f.heroImageUrl.value = await uploadImage(file);
        state.textContent = 'تم الرفع.';
      } catch (e) {
        state.textContent = e.message;
      }
      input.value = '';
    });

    return h('div', { class: 'row' },
      h('button', { class: 'btn ghost sm', onclick: () => input.click() }, 'رفع صورة'),
      state, input);
  })();

  const realCount = (value, overridden) => overridden
    ? `العدد الحقيقي الآن ${num(value)} — والصفحة تعرض رقماً مكتوباً بدلاً منه.`
    : `يُعرض العدد الحقيقي: ${num(value)}`;

  return h('div', {},
    pageHead('Page content', 'كل كلمة في الصفحة الرئيسية للزوار. تُحفظ فوراً وتظهر عند إعادة التحميل.',
      h('a', { class: 'btn ghost', href: '/', target: '_blank', rel: 'noopener' }, 'فتح الصفحة'),
      h('button', { class: 'btn', onclick: save }, 'حفظ')),

    h('div', { class: 'card' },
      sectionHead('الواجهة الأولى', 'gavel', 'gold'),
      siteField(f, 'heroBadge', 'الشريط الصغير فوق العنوان', s.heroBadge,
        { hint: 'اتركه فارغاً لإخفائه.' }),
      siteField(f, 'heroTitle', 'العنوان الرئيسي', s.heroTitle),
      siteField(f, 'heroSubtitle', 'الجملة تحت العنوان', s.heroSubtitle, { long: true }),
      h('div', { class: 'grid-2' },
        siteField(f, 'heroCtaLabel', 'نص الزر', s.heroCtaLabel),
        siteField(f, 'heroCtaUrl', 'رابط الزر', s.heroCtaUrl,
          { hint: 'اتركه فارغاً لينزل الزائر إلى قسم التحميل.' })),
      siteField(f, 'heroImageUrl', 'صورة الواجهة', s.heroImageUrl),
      heroPicker),

    h('div', { class: 'card' },
      sectionHead('روابط التطبيق', 'box', 'info'),
      h('div', { class: 'grid-2' },
        siteField(f, 'appStoreUrl', 'App Store', s.appStoreUrl),
        siteField(f, 'googlePlayUrl', 'Google Play', s.googlePlayUrl)),
      h('div', { class: 'hint' }, 'الزر الذي لا رابط له لا يظهر في الصفحة إطلاقاً.')),

    h('div', { class: 'card' },
      sectionHead('من نحن', 'shield', 'ok'),
      siteToggle(f, 'aboutEnabled', 'هذا القسم', s.aboutEnabled),
      h('div', { class: 'grid-2' },
        siteField(f, 'aboutEyebrow', 'الشريط الصغير', s.aboutEyebrow),
        siteField(f, 'aboutTitle', 'عنوان القسم', s.aboutTitle)),
      h('div', { class: 'grid-2' },
        siteField(f, 'featuresTitle', 'عنوان المميزات', s.featuresTitle),
        siteField(f, 'visionTitle', 'عنوان الرؤية', s.visionTitle)),
      h('div', { class: 'grid-2' },
        siteField(f, 'featuresBody', 'نص المميزات', s.featuresBody, { long: true, rows: 4 }),
        siteField(f, 'visionBody', 'نص الرؤية', s.visionBody, { long: true, rows: 4 }))),

    h('div', { class: 'card' },
      sectionHead('الأرقام', 'coins', 'warn'),
      siteToggle(f, 'statsEnabled', 'هذا القسم', s.statsEnabled),
      h('div', { class: 'hint' },
        'الأرقام تُحسب من قاعدة البيانات. اكتب رقماً في الخانة إن أردت نشر رقم مختلف، '
        + 'واترك الخانة فارغة ليُعرض العدد الحقيقي.'),
      h('div', { class: 'grid-3' },
        siteField(f, 'statUsersLabel', 'وصف المستخدمين', s.statUsersLabel),
        siteField(f, 'statAuctionsLabel', 'وصف المزادات', s.statAuctionsLabel),
        siteField(f, 'statProductsLabel', 'وصف المنتجات', s.statProductsLabel)),
      h('div', { class: 'grid-3' },
        siteField(f, 'statUsersOverride', 'رقم مكتوب للمستخدمين', s.statUsersOverride,
          { type: 'number', hint: realCount(stats.users, stats.usersOverridden) }),
        siteField(f, 'statAuctionsOverride', 'رقم مكتوب للمزادات', s.statAuctionsOverride,
          { type: 'number', hint: realCount(stats.auctions, stats.auctionsOverridden) }),
        siteField(f, 'statProductsOverride', 'رقم مكتوب للمنتجات', s.statProductsOverride,
          { type: 'number', hint: realCount(stats.products, stats.productsOverridden) }))),

    h('div', { class: 'card' },
      sectionHead('عناوين بقية الأقسام', 'layers', 'violet'),
      h('div', { class: 'hint' },
        'البطاقات نفسها تُضاف من صفحة «الخطوات والبطاقات»، والآراء والأسئلة من صفحتيهما.'),

      h('div', { class: 'grid-3' },
        siteToggle(f, 'stepsEnabled', 'قسم الخطوات', s.stepsEnabled),
        siteToggle(f, 'participateEnabled', 'قسم المشاركة', s.participateEnabled),
        siteToggle(f, 'showcaseEnabled', 'قسم الصور', s.showcaseEnabled)),
      h('div', { class: 'grid-3' },
        siteField(f, 'showcaseEyebrow', 'شريط الصور', s.showcaseEyebrow),
        siteField(f, 'showcaseTitle', 'عنوان الصور', s.showcaseTitle),
        siteField(f, 'showcaseSubtitle', 'جملة الصور', s.showcaseSubtitle)),
      h('div', { class: 'grid-2' },
        siteField(f, 'stepsEyebrow', 'شريط الخطوات', s.stepsEyebrow),
        siteField(f, 'participateEyebrow', 'شريط المشاركة', s.participateEyebrow)),
      h('div', { class: 'grid-2' },
        siteField(f, 'stepsTitle', 'عنوان الخطوات', s.stepsTitle),
        siteField(f, 'participateTitle', 'عنوان المشاركة', s.participateTitle)),
      h('div', { class: 'grid-2' },
        siteField(f, 'stepsSubtitle', 'جملة الخطوات', s.stepsSubtitle, { long: true }),
        siteField(f, 'participateSubtitle', 'جملة المشاركة', s.participateSubtitle, { long: true })),

      h('div', { class: 'grid-2' },
        siteToggle(f, 'testimonialsEnabled', 'قسم الآراء', s.testimonialsEnabled),
        siteToggle(f, 'faqEnabled', 'قسم الأسئلة', s.faqEnabled)),
      h('div', { class: 'grid-2' },
        siteField(f, 'testimonialsEyebrow', 'شريط الآراء', s.testimonialsEyebrow),
        siteField(f, 'faqEyebrow', 'شريط الأسئلة', s.faqEyebrow)),
      h('div', { class: 'grid-2' },
        siteField(f, 'testimonialsTitle', 'عنوان الآراء', s.testimonialsTitle),
        siteField(f, 'faqTitle', 'عنوان الأسئلة', s.faqTitle)),
      h('div', { class: 'grid-2' },
        siteField(f, 'testimonialsSubtitle', 'جملة الآراء', s.testimonialsSubtitle, { long: true }),
        siteField(f, 'faqSubtitle', 'جملة الأسئلة', s.faqSubtitle, { long: true }))),

    h('div', { class: 'card' },
      sectionHead('شريط التحميل الأخير', 'plus', 'gold'),
      siteToggle(f, 'downloadEnabled', 'هذا القسم', s.downloadEnabled),
      siteField(f, 'downloadTitle', 'العنوان', s.downloadTitle),
      siteField(f, 'downloadSubtitle', 'الجملة', s.downloadSubtitle, { long: true })),

    h('div', { class: 'card' },
      sectionHead('التذييل والتواصل', 'users', 'info'),
      siteField(f, 'footerNote', 'جملة تعريفية في التذييل', s.footerNote, { long: true }),
      h('div', { class: 'grid-2' },
        siteField(f, 'contactEmail', 'البريد الإلكتروني', s.contactEmail),
        siteField(f, 'contactPhone', 'رقم الهاتف', s.contactPhone)),
      h('div', { class: 'grid-2' },
        siteField(f, 'privacyUrl', 'رابط سياسة الخصوصية', s.privacyUrl),
        siteField(f, 'termsUrl', 'رابط الشروط والأحكام', s.termsUrl)),
      h('div', { class: 'grid-3' },
        siteField(f, 'twitterUrl', 'X (تويتر)', s.twitterUrl),
        siteField(f, 'instagramUrl', 'إنستغرام', s.instagramUrl),
        siteField(f, 'tiktokUrl', 'تيك توك', s.tiktokUrl)),
      h('div', { class: 'grid-2' },
        siteField(f, 'snapchatUrl', 'سناب شات', s.snapchatUrl),
        siteField(f, 'whatsappUrl', 'واتساب', s.whatsappUrl)),
      h('div', { class: 'hint' }, 'أي حساب بلا رابط لا تظهر أيقونته.')),

    h('div', { class: 'card' },
      sectionHead('محركات البحث', 'search', 'violet'),
      siteField(f, 'metaTitle', 'عنوان الصفحة في نتائج البحث', s.metaTitle),
      siteField(f, 'metaDescription', 'وصف الصفحة في نتائج البحث', s.metaDescription,
        { long: true, hint: 'جملة أو جملتان، نحو ١٦٠ حرفاً.' })),

    h('div', { class: 'card' },
      error,
      h('div', { class: 'row' }, h('button', { class: 'btn', onclick: save }, 'حفظ'))));
}

/** The names the landing page knows how to draw. */
const SITE_ICONS = ['gavel', 'coins', 'gift', 'cart', 'users', 'clock', 'wallet',
  'truck', 'shield', 'check', 'search', 'spark', 'phone'];

/*
 * Add or edit one card.
 *
 * Written as its own dialog rather than through formModal because a picture is picked from the
 * machine, and a file input cannot be described as a row in a list of text fields.
 *
 * `kind` decides what the dialog insists on: a picture card is the picture, so it will not save
 * without one, while a step or a participate card may use either a picture or an icon.
 */
function siteCardDialog({ card, kind, onSaved }) {
  const isShowcase = kind === 'Showcase';

  const back = h('div', { class: 'modal-back' });
  const error = h('div', { class: 'err-text' });

  const title = h('input', { value: card?.title || '', placeholder: 'عنوان قصير' });
  const body = h('textarea', { rows: '3' }, card?.body || '');
  const order = h('input', { type: 'number', value: card?.displayOrder ?? 0 });

  const iconSelect = h('select', {}, ...SITE_ICONS.map((i) =>
    h('option', { value: i, selected: i === (card?.icon || 'spark') ? '' : null }, i)));

  const published = h('select', {},
    h('option', { value: 'true', selected: card && !card.isPublished ? null : '' }, 'ظاهرة'),
    h('option', { value: 'false', selected: card && !card.isPublished ? '' : null }, 'مخفية'));

  let imageUrl = card?.imageUrl || null;

  const thumb = h('div', { class: 'prize-thumb empty' });
  const fileInput = h('input', { type: 'file', accept: 'image/*' });
  const pick = h('button', { class: 'btn ghost sm', type: 'button' }, 'اختر صورة');
  const drop = h('button', { class: 'btn ghost sm', type: 'button' }, 'إزالة الصورة');
  const fileState = h('div', { class: 'hint' }, 'JPEG أو PNG أو GIF · حتى ٥ ميجابايت');

  fileInput.hidden = true;

  // The icon only matters while there is no picture, so the field goes away once there is one.
  const iconField = h('div', { class: 'field' }, h('label', {}, 'الأيقونة'), iconSelect);

  const paint = () => {
    thumb.replaceChildren();
    if (imageUrl) {
      thumb.className = 'prize-thumb';
      thumb.append(h('img', { src: imageUrl, alt: '' }));
    } else {
      thumb.className = 'prize-thumb empty';
      thumb.append(icon('box'));
    }
    drop.disabled = imageUrl ? null : '';
    iconField.hidden = !!imageUrl;
  };

  pick.addEventListener('click', () => fileInput.click());
  drop.addEventListener('click', () => {
    imageUrl = null;
    paint();
    fileState.textContent = 'لا توجد صورة';
  });

  fileInput.addEventListener('change', async () => {
    const file = fileInput.files?.[0];
    if (!file) return;

    error.textContent = '';
    pick.disabled = '';
    fileState.textContent = 'جارٍ الرفع…';

    try {
      imageUrl = await uploadImage(file);
      paint();
      fileState.textContent = 'تم رفع الصورة';
    } catch (e) {
      fileState.textContent = '';
      error.textContent = e.message;
    } finally {
      pick.disabled = null;
      // Clearing lets the same file be picked again after a failure.
      fileInput.value = '';
    }
  });

  const save = async () => {
    error.textContent = '';

    if (!title.value.trim()) { error.textContent = 'العنوان مطلوب.'; return; }
    if (isShowcase && !imageUrl) { error.textContent = 'بطاقة الصور تحتاج إلى صورة.'; return; }

    const payload = {
      kind,
      title: title.value.trim(),
      body: body.value.trim() || null,
      icon: imageUrl ? null : iconSelect.value,
      imageUrl,
      displayOrder: Number(order.value || 0),
      isPublished: published.value === 'true',
    };

    try {
      if (card) {
        await api(`/admin/site/cards/${card.id}`, { method: 'PUT', body: JSON.stringify(payload) });
      } else {
        await api('/admin/site/cards', { method: 'POST', body: JSON.stringify(payload) });
      }
      back.remove();
      onSaved();
    } catch (e) {
      error.textContent = e.message + (e.details?.length ? ` — ${e.details.join('; ')}` : '');
    }
  };

  paint();

  back.append(h('div', { class: 'modal' },
    h('h3', {}, card ? 'تعديل البطاقة' : 'إضافة بطاقة'),
    h('p', { class: 'sub' }, isShowcase
      ? 'الصورة تظهر في الصفحة والنص تحتها مباشرة.'
      : 'اختر صورة أو اكتفِ بأيقونة.'),

    h('div', { class: 'prize-dialog' },
      h('div', {},
        thumb,
        h('div', { class: 'row', style: 'margin-top:10px' }, pick, drop),
        fileState),
      h('div', {},
        h('div', { class: 'field' }, h('label', {}, 'العنوان'), title),
        h('div', { class: 'field' }, h('label', {}, 'النص تحت الصورة'), body),
        iconField,
        h('div', { class: 'grid-2' },
          h('div', { class: 'field' }, h('label', {}, 'ترتيب العرض'), order),
          h('div', { class: 'field' }, h('label', {}, 'الحالة'), published)))),

    fileInput,
    error,
    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'إلغاء'),
      h('button', { class: 'btn', onclick: save }, 'حفظ'))));

  document.body.append(back);
  title.focus();
}

async function viewSiteCards() {
  const data = await api('/admin/site/content');

  const edit = (card, kind) => siteCardDialog({
    card,
    kind,
    onSaved: () => { toast('تم حفظ البطاقة.', 'ok'); render(); },
  });

  const remove = (card) => confirmDialog('حذف البطاقة', `سيُحذف «${card.title}» من الصفحة.`,
    async () => {
      await api(`/admin/site/cards/${card.id}`, { method: 'DELETE' });
      toast('تم الحذف.', 'ok');
      render();
    }, { danger: true });

  const table = (rows, kind) => rows.length
    ? h('div', { class: 'table-wrap' }, h('table', {},
        h('thead', {}, h('tr', {},
          h('th', { class: 'num' }, 'الترتيب'), h('th', {}, 'الصورة'), h('th', {}, 'العنوان'),
          h('th', {}, 'الحالة'), h('th', {}, ''))),
        h('tbody', {}, ...rows.map((c) => {
          const titleCell = h('td', {});
          titleCell.append(document.createTextNode(c.title));
          if (c.body) titleCell.append(h('div', { class: 'hint' }, document.createTextNode(c.body)));

          return h('tr', {},
            h('td', { class: 'num' }, c.displayOrder),
            // The picture when there is one, otherwise the icon the card falls back to.
            h('td', {}, c.imageUrl
              ? prizeThumb(c.imageUrl)
              : h('span', { class: 'card-head-icon' }, icon(c.icon || 'spark'))),
            titleCell,
            h('td', {}, h('span', { class: c.isPublished ? 'badge live' : 'badge warn' },
              c.isPublished ? 'ظاهرة' : 'مخفية')),
            h('td', {}, h('div', { class: 'row' },
              h('button', { class: 'btn ghost sm', onclick: () => edit(c, kind) }, 'تعديل'),
              h('button', { class: 'btn danger sm', onclick: () => remove(c) }, 'حذف'))));
        }))))
    : h('div', { class: 'empty' }, 'لا توجد بطاقات بعد.');

  return h('div', {},
    pageHead('Steps & cards', 'البطاقات التي تشرح للزائر كيف يشارك. القسم بلا بطاقات لا يظهر.'),

    h('div', { class: 'card' },
      sectionHead('كيف تعمل — خطوات مرقّمة', 'clock', 'info'),
      h('div', { class: 'toolbar' },
        h('button', { class: 'btn', onclick: () => edit(null, 'Step') },
          icon('plus'), h('span', {}, 'إضافة خطوة'))),
      table(data.steps, 'Step')),

    h('div', { class: 'card' },
      sectionHead('طريقة المشاركة', 'users', 'ok'),
      h('div', { class: 'toolbar' },
        h('button', { class: 'btn', onclick: () => edit(null, 'Participate') },
          icon('plus'), h('span', {}, 'إضافة بطاقة'))),
      table(data.participate, 'Participate')),

    h('div', { class: 'card' },
      sectionHead('الصور — صورة ونص تحتها', 'box', 'gold'),
      h('div', { class: 'toolbar' },
        h('button', { class: 'btn', onclick: () => edit(null, 'Showcase') },
          icon('plus'), h('span', {}, 'إضافة صورة')),
        h('span', { class: 'hint' },
          'ثلاث صور تُعرض جنباً إلى جنب، وتحت كل صورة عنوانها وشرحها. '
          + 'أضف ما شئت؛ الصفحة ترتّبها في صفوف.')),
      table(data.showcase, 'Showcase')));
}

/*
 * The advertisements on the app's front page.
 *
 * Three places, and each one is a different promise to the person holding the phone: a thin strip
 * under the search, the large moving pictures under it, and one still picture under the
 * departments. They are listed here in that order, so the page reads the way the app looks.
 *
 * With a place left empty the app falls back to what it did before this page existed — auction
 * covers and product photographs — so an empty list is a working front page, not a broken one.
 */
const AD_PLACES = [
  {
    value: 'HomeStrip',
    label: 'الشريط العلوي',
    note: 'تحت صندوق البحث · صورة عريضة قصيرة، تتبدّل وحدها. لا يظهر عليها أي نص.',
  },
  {
    value: 'HomeSlideshow',
    label: 'الإعلان الكبير',
    note: 'تحت الشريط · صور كبيرة تتبدّل وحدها، ويظهر عليها العنوان والسطر الصغير.',
  },
  {
    value: 'HomeUnderCategories',
    label: 'إعلان تحت الأقسام',
    note: 'صورة واحدة ثابتة تحت «تسوق حسب القسم». تُعرض أول صورة فقط.',
  },
];

/** The value of the "what does it open" list, as one string, so one <select> covers four kinds. */
function adTargetValue(ad) {
  if (!ad || ad.targetKind === 'None') return 'None';

  // These two carry a typed value rather than the id of something on the platform.
  if (ad.targetKind === 'Url' || ad.targetKind === 'WhatsApp') return ad.targetKind;

  return `${ad.targetKind}:${ad.targetId}`;
}

/** Turns that one string back into the two fields the API takes. */
function adTargetFields(value, url) {
  if (value === 'None') return { targetKind: 'None', targetId: null, targetUrl: null };

  // Both read the same box: an address for one, a mobile number for the other. The server checks
  // which of the two it actually got.
  if (value === 'Url' || value === 'WhatsApp') {
    return { targetKind: value, targetId: null, targetUrl: url || null };
  }

  const [kind, id] = value.split(':');
  return { targetKind: kind, targetId: id, targetUrl: null };
}

/** An instant as the browser's date-and-time box wants it, in the administrator's own time. */
function adLocalInput(iso) {
  if (!iso) return '';

  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';

  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
    + `T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** And back again: a local wall time becomes the instant the server stores. */
function adIsoFromInput(value) {
  if (!value) return null;

  const d = new Date(value);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

async function viewAppAds() {
  // The three lists behind the "what does it open" box. Enough of each to choose from without
  // turning the dialog into a search screen.
  const [ads, auctions, products, categories] = await Promise.all([
    api('/admin/ads'),
    api('/admin/auctions?page=1&pageSize=50'),
    api('/admin/store/products?page=1&pageSize=50'),
    api('/admin/store/categories'),
  ]);

  const targetOptions = [
    { value: 'None', label: 'لا شيء — الصورة فقط' },
    { value: 'Url', label: 'رابط خارجي (اكتبه تحت)' },
    { value: 'WhatsApp', label: 'محادثة واتساب (اكتب الرقم تحت)' },
    ...auctions.items.map((a) => ({ value: `Auction:${a.id}`, label: `مزاد · ${a.title}` })),
    ...products.items.map((p) => ({ value: `Product:${p.id}`, label: `منتج · ${p.name}` })),
    ...orderedCategories(categories).map((c) => ({ value: `Category:${c.id}`, label: `قسم · ${categoryLabel(c)}` })),
  ];

  const edit = (ad, place) => formModal(
    ad ? 'تعديل الإعلان' : 'إعلان جديد',
    'الصورة هي الإعلان. اختر مكانها، وما تفتحه عند الضغط عليها.',
    [
      {
        name: 'placement', label: 'مكان الإعلان', type: 'select',
        value: ad?.placement || place || 'HomeStrip',
        options: AD_PLACES.map((p) => ({ value: p.value, label: p.label })),
      },
      { name: 'imageUrl', label: 'الصورة', type: 'image', value: ad?.imageUrl || '' },
      {
        name: 'title', label: 'العنوان (الإعلان الكبير فقط)', value: ad?.title || '',
        hint: 'يُكتب فوق الصورة في الإعلان الكبير. الشريطان الآخران يعرضان الصورة وحدها.',
      },
      { name: 'note', label: 'السطر الصغير', value: ad?.note || '' },
      {
        name: 'target', label: 'ماذا يفتح عند الضغط؟', type: 'select',
        value: adTargetValue(ad), options: targetOptions,
      },
      {
        name: 'targetUrl', label: 'الرابط الخارجي أو رقم الواتساب', value: ad?.targetUrl || '',
        hint: 'مع «رابط خارجي» اكتب العنوان كاملاً. مع «محادثة واتساب» اكتب رقم جوال سعودي '
          + 'مثل 0501234567، وسيفتح الضغط على الإعلان محادثة مع هذا الرقم.',
      },
      { name: 'displayOrder', label: 'ترتيب العرض', type: 'number', value: ad?.displayOrder ?? 0 },
      {
        name: 'isPublished', label: 'الحالة', type: 'select',
        value: ad ? String(ad.isPublished) : 'true',
        options: [{ value: 'true', label: 'منشور' }, { value: 'false', label: 'مسودة' }],
      },
      { name: 'startsAt', label: 'يبدأ في', type: 'datetime-local', value: adLocalInput(ad?.startsAt),
        hint: 'اتركه فارغاً ليظهر فور نشره.' },
      { name: 'endsAt', label: 'ينتهي في', type: 'datetime-local', value: adLocalInput(ad?.endsAt),
        hint: 'اتركه فارغاً ليستمر حتى توقفه.' },
    ],
    async (v) => {
      const body = {
        placement: v.placement,
        imageUrl: v.imageUrl,
        title: v.title || null,
        note: v.note || null,
        ...adTargetFields(v.target, v.targetUrl),
        displayOrder: Number(v.displayOrder) || 0,
        isPublished: v.isPublished === 'true',
        startsAt: adIsoFromInput(v.startsAt),
        endsAt: adIsoFromInput(v.endsAt),
      };

      if (ad) await api(`/admin/ads/${ad.id}`, { method: 'PUT', body: JSON.stringify(body) });
      else await api('/admin/ads', { method: 'POST', body: JSON.stringify(body) });

      toast('تم حفظ الإعلان.', 'ok');
      render();
    });

  const remove = (ad) => confirmDialog('حذف الإعلان', 'سيُحذف الإعلان من التطبيق نهائياً.',
    async () => {
      await api(`/admin/ads/${ad.id}`, { method: 'DELETE' });
      toast('تم الحذف.', 'ok');
      render();
    }, { danger: true });

  const targetLabel = (ad) => {
    const value = adTargetValue(ad);
    if (value === 'Url') return ad.targetUrl || 'رابط';

    const found = targetOptions.find((o) => o.value === value);
    return found ? found.label : (ad.route || 'لا شيء');
  };

  const table = (place) => {
    const rows = ads.filter((a) => a.placement === place.value);

    if (!rows.length) {
      return h('div', { class: 'empty' },
        'لا يوجد إعلان هنا. التطبيق يعرض صور المزادات والمنتجات بدلاً منه.');
    }

    return h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {},
        h('th', { class: 'num' }, 'الترتيب'), h('th', {}, 'الصورة'), h('th', {}, 'العنوان'),
        h('th', {}, 'يفتح'), h('th', {}, 'المدة'), h('th', {}, 'الحالة'), h('th', {}, ''))),
      h('tbody', {}, ...rows.map((ad) => {
        const when = [
          ad.startsAt ? `من ${when(ad.startsAt)}` : null,
          ad.endsAt ? `حتى ${when(ad.endsAt)}` : null,
        ].filter(Boolean).join(' · ') || 'دائم';

        const title = h('td', {});
        title.append(document.createTextNode(ad.title || '—'));
        if (ad.note) title.append(h('div', { class: 'hint' }, document.createTextNode(ad.note)));

        return h('tr', {},
          h('td', { class: 'num' }, ad.displayOrder),
          h('td', {}, h('img', { src: ad.imageUrl, alt: '', class: 'cover-preview sm' })),
          title,
          h('td', {}, document.createTextNode(targetLabel(ad))),
          h('td', {}, when),
          h('td', {}, h('span', { class: ad.isPublished ? 'badge live' : 'badge warn' },
            ad.isPublished ? 'منشور' : 'مسودة')),
          h('td', {}, h('div', { class: 'row' },
            h('button', { class: 'btn ghost sm', onclick: () => edit(ad) }, 'تعديل'),
            h('button', { class: 'btn danger sm', onclick: () => remove(ad) }, 'حذف'))));
      }))));
  };

  return h('div', {},
    pageHead('App ads', 'الصور الإعلانية في الصفحة الرئيسية للتطبيق.',
      h('button', { class: 'btn', onclick: () => edit(null) }, icon('plus'), h('span', {}, 'إعلان جديد'))),

    ...AD_PLACES.map((place) => h('div', { class: 'card' },
      sectionHead(place.label, 'image', 'info'),
      h('div', { class: 'toolbar' },
        h('button', { class: 'btn ghost sm', onclick: () => edit(null, place.value) },
          icon('plus'), h('span', {}, 'إضافة صورة')),
        h('span', { class: 'hint' }, place.note)),
      table(place))));
}

async function viewSiteTestimonials() {
  const data = await api('/admin/site/content');

  const edit = (t) => formModal(
    t ? 'تعديل الرأي' : 'إضافة رأي',
    'اكتب فقط آراءً قالها أصحابها فعلاً وأذنوا بنشرها.',
    [
      { name: 'name', label: 'الاسم', value: t?.name },
      { name: 'role', label: 'تحت الاسم (المدينة مثلاً)', value: t?.role },
      { name: 'body', label: 'نص الرأي', value: t?.body },
      { name: 'rating', label: 'التقييم من ٥', type: 'number', value: t?.rating ?? 5 },
      { name: 'avatarUrl', label: 'رابط الصورة', value: t?.avatarUrl,
        hint: 'اتركه فارغاً ليظهر أول حرف من الاسم بدلاً من الصورة.' },
      { name: 'displayOrder', label: 'ترتيب العرض', type: 'number', value: t?.displayOrder ?? 0 },
      {
        name: 'isPublished', label: 'الحالة', type: 'select',
        value: t ? String(t.isPublished) : 'true',
        options: [{ value: 'true', label: 'منشور' }, { value: 'false', label: 'مسودة' }],
      },
    ],
    async (v) => {
      const body = {
        name: v.name,
        body: v.body,
        rating: Number(v.rating),
        role: v.role || null,
        avatarUrl: v.avatarUrl || null,
        displayOrder: Number(v.displayOrder),
        isPublished: v.isPublished === 'true',
      };

      if (t) await api(`/admin/site/testimonials/${t.id}`, { method: 'PUT', body: JSON.stringify(body) });
      else await api('/admin/site/testimonials', { method: 'POST', body: JSON.stringify(body) });

      toast('تم حفظ الرأي.', 'ok');
      render();
    });

  const remove = (t) => confirmDialog('حذف الرأي', `سيُحذف رأي «${t.name}» نهائياً.`,
    async () => {
      await api(`/admin/site/testimonials/${t.id}`, { method: 'DELETE' });
      toast('تم الحذف.', 'ok');
      render();
    }, { danger: true });

  return h('div', {},
    pageHead('Reviews', 'ما يقوله المستخدمون عن التجربة. القسم يختفي من الصفحة ما لم يوجد رأي منشور.',
      h('button', { class: 'btn', onclick: () => edit(null) }, icon('plus'), h('span', {}, 'إضافة رأي'))),

    h('div', { class: 'card' },
      sectionHead('الآراء المنشورة', 'quote', 'gold'),
      data.testimonials.length
        ? h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {},
              h('th', { class: 'num' }, 'الترتيب'), h('th', {}, 'الاسم'), h('th', {}, 'الرأي'),
              h('th', { class: 'num' }, 'التقييم'), h('th', {}, 'الحالة'), h('th', {}, ''))),
            h('tbody', {}, ...data.testimonials.map((t) => {
              const who = h('td', {});
              who.append(document.createTextNode(t.name));
              if (t.role) who.append(h('div', { class: 'hint' }, document.createTextNode(t.role)));

              const body = h('td', {});
              body.append(document.createTextNode(t.body));

              return h('tr', {},
                h('td', { class: 'num' }, t.displayOrder),
                who,
                body,
                h('td', { class: 'num' }, `${t.rating} / 5`),
                h('td', {}, h('span', { class: t.isPublished ? 'badge live' : 'badge warn' },
                  t.isPublished ? 'منشور' : 'مسودة')),
                h('td', {}, h('div', { class: 'row' },
                  h('button', { class: 'btn ghost sm', onclick: () => edit(t) }, 'تعديل'),
                  h('button', { class: 'btn danger sm', onclick: () => remove(t) }, 'حذف'))));
            }))))
        : h('div', { class: 'empty' },
            'لا توجد آراء بعد. القسم مخفي في الصفحة حتى تُضاف آراء حقيقية.')));
}

async function viewSiteFaqs() {
  const data = await api('/admin/site/content');

  const edit = (q) => formModal(q ? 'تعديل السؤال' : 'إضافة سؤال', null, [
    { name: 'question', label: 'السؤال', value: q?.question },
    { name: 'answer', label: 'الإجابة', value: q?.answer },
    { name: 'displayOrder', label: 'ترتيب العرض', type: 'number', value: q?.displayOrder ?? 0 },
    {
      name: 'isPublished', label: 'الحالة', type: 'select',
      value: q ? String(q.isPublished) : 'true',
      options: [{ value: 'true', label: 'منشور' }, { value: 'false', label: 'مسودة' }],
    },
  ], async (v) => {
    const body = {
      question: v.question,
      answer: v.answer,
      displayOrder: Number(v.displayOrder),
      isPublished: v.isPublished === 'true',
    };

    if (q) await api(`/admin/site/faqs/${q.id}`, { method: 'PUT', body: JSON.stringify(body) });
    else await api('/admin/site/faqs', { method: 'POST', body: JSON.stringify(body) });

    toast('تم حفظ السؤال.', 'ok');
    render();
  });

  const remove = (q) => confirmDialog('حذف السؤال', 'سيُحذف السؤال وإجابته من الصفحة.',
    async () => {
      await api(`/admin/site/faqs/${q.id}`, { method: 'DELETE' });
      toast('تم الحذف.', 'ok');
      render();
    }, { danger: true });

  return h('div', {},
    pageHead('Questions', 'الأسئلة الشائعة أسفل الصفحة الرئيسية.',
      h('button', { class: 'btn', onclick: () => edit(null) }, icon('plus'), h('span', {}, 'إضافة سؤال'))),

    h('div', { class: 'card' },
      sectionHead('الأسئلة', 'help', 'info'),
      data.faqs.length
        ? h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {},
              h('th', { class: 'num' }, 'الترتيب'), h('th', {}, 'السؤال'), h('th', {}, 'الإجابة'),
              h('th', {}, 'الحالة'), h('th', {}, ''))),
            h('tbody', {}, ...data.faqs.map((q) => {
              const question = h('td', {});
              question.append(document.createTextNode(q.question));

              const answer = h('td', {});
              answer.append(document.createTextNode(q.answer));

              return h('tr', {},
                h('td', { class: 'num' }, q.displayOrder),
                question,
                answer,
                h('td', {}, h('span', { class: q.isPublished ? 'badge live' : 'badge warn' },
                  q.isPublished ? 'منشور' : 'مسودة')),
                h('td', {}, h('div', { class: 'row' },
                  h('button', { class: 'btn ghost sm', onclick: () => edit(q) }, 'تعديل'),
                  h('button', { class: 'btn danger sm', onclick: () => remove(q) }, 'حذف'))));
            }))))
        : h('div', { class: 'empty' }, 'لا توجد أسئلة بعد.')));
}

// ---------------------------------------------------------------- store

async function viewStoreOverview() {
  const o = await api('/admin/store/overview');

  return h('div', {},
    pageHead('Store overview', 'حالة المتجر والمخزون والطلبات في لمحة.'),
    h('div', { class: 'kpi-grid' },
      kpi('Products', num(o.totalProducts)),
      kpi('Active', num(o.activeProducts)),
      kpi('Out of stock', num(o.outOfStockProducts)),
      kpi('Low stock', num(o.lowStockProducts))),
    h('div', { class: 'kpi-grid' },
      kpi('Orders', num(o.totalOrders)),
      kpi('Pending', num(o.pendingOrders)),
      kpi('Completed', num(o.completedOrders)),
      kpi('Store revenue', money(o.storeRevenue, o.currency), true)),
    h('div', { class: 'card' },
      h('h3', {}, 'Shopping credit'),
      h('div', { class: 'kpi-grid' },
        kpi('Issued', money(o.shoppingCreditIssued, o.currency), true),
        kpi('Used', money(o.shoppingCreditUsed, o.currency), true),
        kpi('Outstanding', money(o.shoppingCreditOutstanding, o.currency), true))));
}

async function viewStoreProducts() {
  const q = state.cache.products || (state.cache.products = { page: 1, search: '', status: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);
  if (q.status) params.set('status', q.status);

  const [data, categories] = await Promise.all([
    api(`/admin/store/products?${params}`),
    api('/admin/store/categories'),
  ]);

  const search = h('input', { placeholder: 'Name, SKU or category', value: q.search });
  const status = h('select', {}, ...[['', 'Any status'], ['Draft', 'Draft'], ['Active', 'Active'],
    ['OutOfStock', 'Out of stock'], ['Hidden', 'Hidden'], ['Archived', 'Archived']]
    .map(([v, l]) => h('option', { value: v, selected: v === q.status ? '' : null }, l)));

  const setStatus = async (id, action) => {
    try {
      await api(`/admin/store/products/${id}/${action}`, { method: 'POST' });
      toast('Product updated.', 'ok');
      render();
    } catch (e) { toast(e.message, 'err'); }
  };

  return h('div', {},
    pageHead('Store products', 'Products referenced by an order are archived rather than deleted.',
      h('button', { class: 'btn', onclick: () => productForm(null, categories) }, 'Add product')),

    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, status,
        h('button', {
          class: 'btn ghost sm',
          onclick: () => { q.search = search.value; q.status = status.value; q.page = 1; render(); },
        }, 'Apply')),

      pagedTable({
        columns: [
          { label: 'Product', render: (p) => p.name },
          { label: 'SKU', mono: true, render: (p) => p.sku },
          { label: 'Category', render: (p) => p.categoryName || '—' },
          { label: 'Price', num: true, render: (p) => money(p.price, p.currency) },
          { label: 'Available', num: true, render: (p) => num(p.availableQuantity) },
          { label: 'Pays with', render: (p) => h('span', { class: 'badge info' }, p.paymentMode) },
          { label: 'Status', render: (p) => h('span', { class: badgeClass(p.status) }, p.status) },
          {
            label: '',
            render: (p) => h('div', { class: 'row' },
              h('button', { class: 'btn ghost sm', onclick: () => editProduct(p.id, categories) }, 'Edit'),
              p.status !== 'Active'
                ? h('button', { class: 'btn ghost sm', onclick: () => setStatus(p.id, 'activate') }, 'Activate')
                : h('button', { class: 'btn ghost sm', onclick: () => setStatus(p.id, 'hide') }, 'Hide'),
              h('button', { class: 'btn ghost sm', onclick: () => setStatus(p.id, 'archive') }, 'Archive')),
          },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function editProduct(id, categories) {
  const p = await api(`/admin/store/products/${id}`);
  productForm(p, categories);
}

function productForm(p, categories) {
  const isNew = !p;
  const catOptions = [{ value: '', label: '— none —' },
    ...orderedCategories(categories).map((c) => ({ value: c.id, label: categoryLabel(c) }))];

  formModal(isNew ? 'Add product' : `Edit ${p.name}`,
    isNew ? 'New products start as drafts; activate them when they are ready to sell.'
          : 'Changing the price never rewrites existing orders: those hold their own snapshots.',
    [
      { name: 'name', label: 'Name', value: p?.name },
      { name: 'sku', label: 'SKU', value: p?.sku, placeholder: 'SKU-HEADPHONES-001' },
      { name: 'description', label: 'Description', value: p?.description },
      { name: 'categoryId', label: 'Category', type: 'select', options: catOptions, value: p?.categoryId || '' },
      { name: 'price', label: 'Price', type: 'number', step: '0.01', value: p?.price ?? 0 },
      {
        name: 'compareAtPrice', label: 'Price before offer', type: 'number', step: '0.01',
        value: p?.compareAtPrice ?? '',
        hint: 'اتركه فارغاً إذا لم يكن هناك عرض. يجب أن يكون أعلى من السعر.',
      },
      ...(isNew ? [{ name: 'stockQuantity', label: 'Initial stock', type: 'number', value: 0 }] : []),
      {
        name: 'paymentMode', label: 'Accepted payment', type: 'select', value: p?.paymentMode || 'ShoppingWallet',
        options: [
          { value: 'ShoppingWallet', label: 'Shopping wallet only' },
          { value: 'MainWallet', label: 'Main wallet only' },
          { value: 'Both', label: 'Both wallets' },
        ],
      },
      { name: 'imageUrl', label: 'Product image', type: 'image', value: p?.images?.[0]?.url || '' },
      {
        name: 'isFeatured', label: 'Featured', type: 'select', value: String(p?.isFeatured ?? false),
        options: [{ value: 'false', label: 'No' }, { value: 'true', label: 'Yes' }],
      },
    ],
    async (v) => {
      const body = {
        name: v.name,
        sku: v.sku,
        description: v.description || null,
        categoryId: v.categoryId || null,
        price: Number(v.price),
        // Empty clears the offer; the server refuses anything not above the price.
        compareAtPrice: v.compareAtPrice === '' ? null : Number(v.compareAtPrice),
        paymentMode: v.paymentMode,
        // Not editable in this form; an edit keeps whatever the product already has.
        shippingFee: p?.shippingFee ?? 0,
        lowStockThreshold: p?.lowStockThreshold ?? 5,
        isFeatured: v.isFeatured === 'true',
        requiresShipping: true,
        images: v.imageUrl ? [{ url: v.imageUrl, thumbnailUrl: null, isPrimary: true, displayOrder: 0 }] : null,
      };

      if (isNew) {
        await api('/admin/store/products', {
          method: 'POST',
          body: JSON.stringify({ ...body, stockQuantity: Number(v.stockQuantity), status: 'Draft', currency: 'SAR' }),
        });
      } else {
        await api(`/admin/store/products/${p.id}`, { method: 'PUT', body: JSON.stringify(body) });
      }

      toast('Product saved.', 'ok');
      render();
    });
}

/*
 * Departments come in two levels: main departments, and sub-departments under them. These put a
 * flat list from the server into that shape: each main department followed by its own
 * sub-departments, and a label that says where a sub-department sits ("كوابل شحن › سيارة").
 */
function orderedCategories(categories) {
  const mains = categories.filter((c) => !c.parentId);
  const out = [];
  for (const m of mains) {
    out.push(m);
    out.push(...categories.filter((c) => c.parentId === m.id));
  }
  // A sub-department whose main department is not in the list still shows, at the end.
  out.push(...categories.filter((c) => c.parentId && !mains.some((m) => m.id === c.parentId)));
  return out;
}

const categoryLabel = (c) => (c.parentName ? `${c.parentName} › ${c.name}` : c.name);

async function viewStoreCategories() {
  const categories = await api('/admin/store/categories');

  const hasChildren = (c) => !!c && categories.some((x) => x.parentId === c.id);

  const save = (c) => formModal(c ? 'Edit category' : 'Add category', null, [
    { name: 'name', label: 'Name', value: c?.name },
    {
      name: 'parentId',
      label: 'Main department',
      type: 'select',
      value: c?.parentId || '',
      // Only main departments can be parents, and never the department itself. One that already
      // has sub-departments stays a main department.
      options: [{ value: '', label: '— قسم رئيسي (بدون أب) —' },
        ...(hasChildren(c) ? [] : categories
          .filter((x) => !x.parentId && x.id !== c?.id)
          .map((x) => ({ value: x.id, label: x.name })))],
      hint: hasChildren(c)
        ? 'لهذا القسم أقسام فرعية، لذلك يبقى قسماً رئيسياً.'
        : 'اتركه فارغاً لقسم رئيسي، أو اختر قسماً ليصبح هذا قسماً فرعياً تحته.',
    },
    { name: 'description', label: 'Description', value: c?.description },
    { name: 'imageUrl', label: 'Image URL', value: c?.imageUrl },
    { name: 'displayOrder', label: 'Display order', type: 'number', value: c?.displayOrder ?? 0 },
  ], async (v) => {
    const body = {
      name: v.name,
      slug: null,
      description: v.description || null,
      imageUrl: v.imageUrl || null,
      displayOrder: Number(v.displayOrder),
    };
    // On an edit, an empty choice is sent as the empty id, which the server reads as "make it a
    // main department"; leaving the field out would keep the old parent.
    if (c) {
      await api(`/admin/store/categories/${c.id}`, { method: 'PUT', body: JSON.stringify({
        ...body, status: c.status, parentId: v.parentId || '00000000-0000-0000-0000-000000000000' }) });
    } else {
      await api('/admin/store/categories', { method: 'POST', body: JSON.stringify({ ...body, parentId: v.parentId || null }) });
    }
    toast('Category saved.', 'ok');
    render();
  });

  return h('div', {},
    pageHead('Categories', 'المجموعات التي تُرتَّب فيها منتجات المتجر.',
      h('button', { class: 'btn', onclick: () => save(null) }, 'Add category')),
    h('div', { class: 'card' },
      categories.length
        ? h('div', { class: 'table-wrap' }, h('table', {},
            h('thead', {}, h('tr', {}, h('th', {}, 'Name'), h('th', {}, 'Main department'), h('th', {}, 'Slug'),
              h('th', { class: 'num' }, 'Products'), h('th', { class: 'num' }, 'Order'),
              h('th', {}, 'Status'), h('th', {}, ''))),
            h('tbody', {}, ...orderedCategories(categories).map((c) => h('tr', {},
              // Sub-departments sit under their main department, set in.
              c.parentId
                ? h('td', { style: 'padding-inline-start:28px' }, h('span', { class: 'muted' }, '↳ '), c.name)
                : h('td', {}, h('strong', {}, c.name),
                    categories.some((x) => x.parentId === c.id)
                      ? h('span', { class: 'muted' }, ` · ${categories.filter((x) => x.parentId === c.id).length} فرعي`)
                      : null),
              h('td', {}, c.parentName || '—'),
              h('td', { class: 'mono' }, c.slug),
              h('td', { class: 'num' }, num(c.productCount)),
              h('td', { class: 'num' }, c.displayOrder),
              h('td', {}, h('span', { class: badgeClass(c.status) }, c.status)),
              h('td', {}, h('button', { class: 'btn ghost sm', onclick: () => save(c) }, 'Edit')))))))
        : h('div', { class: 'empty' }, 'No categories yet.')));
}

async function viewInventory() {
  const q = state.cache.inventory || (state.cache.inventory = { page: 1, search: '', lowOnly: false });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);
  if (q.lowOnly) params.set('lowStockOnly', 'true');

  const data = await api(`/admin/store/inventory?${params}`);

  const search = h('input', { placeholder: 'Name or SKU', value: q.search });
  const low = h('select', {},
    h('option', { value: '', selected: q.lowOnly ? null : '' }, 'All stock'),
    h('option', { value: '1', selected: q.lowOnly ? '' : null }, 'Low stock only'));

  const adjust = (row) => formModal(`Adjust stock — ${row.name}`,
    `Current stock ${row.stockQuantity}. Adjustments are audited and always leave a transaction.`, [
      { name: 'delta', label: 'Change (+ to restock, − to remove)', type: 'number', value: 0 },
      { name: 'reason', label: 'Reason (required)', placeholder: 'New inventory shipment' },
    ], async (v) => {
      await api(`/admin/store/inventory/${row.productId}/adjust`, {
        method: 'POST',
        body: JSON.stringify({ delta: Number(v.delta), reason: v.reason, type: null }),
      });
      toast('Stock adjusted.', 'ok');
      render();
    });

  return h('div', {},
    pageHead('Inventory', 'Stock only moves alongside an inventory transaction.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, low,
        h('button', {
          class: 'btn ghost sm',
          onclick: () => { q.search = search.value; q.lowOnly = low.value === '1'; q.page = 1; render(); },
        }, 'Apply')),

      pagedTable({
        columns: [
          { label: 'Product', render: (r) => r.name },
          { label: 'SKU', mono: true, render: (r) => r.sku },
          { label: 'Stock', num: true, render: (r) => num(r.stockQuantity) },
          { label: 'Reserved', num: true, render: (r) => num(r.reservedQuantity) },
          { label: 'Available', num: true, render: (r) => num(r.availableQuantity) },
          { label: 'Sold', num: true, render: (r) => num(r.soldQuantity) },
          {
            label: 'Alert',
            render: (r) => r.isLowStock
              ? h('span', { class: 'badge warn' }, `منخفض (≤${r.lowStockThreshold})`)
              : h('span', { class: 'badge ok' }, 'OK'),
          },
          { label: '', render: (r) => h('button', { class: 'btn ghost sm', onclick: () => adjust(r) }, 'Adjust') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function viewOrders() {
  const q = state.cache.orders || (state.cache.orders = { page: 1, search: '', status: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);
  if (q.status) params.set('status', q.status);

  const data = await api(`/admin/store/orders?${params}`);

  const search = h('input', { placeholder: 'Order number or customer', value: q.search });
  const status = h('select', {}, ...[['', 'Any status'], ['Pending', 'Pending'], ['Paid', 'Paid'],
    ['Processing', 'Processing'], ['Shipped', 'Shipped'], ['Delivered', 'Delivered'],
    ['Completed', 'Completed'], ['Cancelled', 'Cancelled'], ['Refunded', 'Refunded']]
    .map(([v, l]) => h('option', { value: v, selected: v === q.status ? '' : null }, l)));

  return h('div', {},
    pageHead('Store orders', 'تابع كل طلب من الدفع حتى التسليم.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search, status,
        h('button', {
          class: 'btn ghost sm',
          onclick: () => { q.search = search.value; q.status = status.value; q.page = 1; render(); },
        }, 'Apply')),

      pagedTable({
        columns: [
          { label: 'Order', mono: true, render: (o) => o.orderNumber },
          { label: 'Customer', render: (o) => o.customerName || shortId(o.userId) },
          { label: 'Items', num: true, render: (o) => o.items.length },
          { label: 'Total', num: true, render: (o) => money(o.total, o.currency) },
          { label: 'Paid with', render: (o) => h('span', { class: 'badge info' }, o.paymentMethod) },
          { label: 'Status', render: (o) => h('span', { class: badgeClass(o.status) }, o.status) },
          { label: 'Created', render: (o) => when(o.createdAt) },
          { label: '', render: (o) => h('button', { class: 'btn ghost sm', onclick: () => openOrder(o.id) }, 'Open') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function openOrder(id) {
  const o = await api(`/admin/store/orders/${id}`);
  const back = h('div', { class: 'modal-back' });

  const act = async (path, body, message) => {
    try {
      await api(`/admin/store/orders/${o.id}/${path}`, { method: 'POST', body: JSON.stringify(body || {}) });
      back.remove();
      toast(message, 'ok');
      render();
    } catch (e) { toast(e.message, 'err'); }
  };

  const next = {
    Paid: [['process', 'Mark processing']],
    Processing: [['ship', 'Mark shipped']],
    Shipped: [['deliver', 'Mark delivered']],
    Delivered: [['complete', 'Mark completed']],
  }[o.status] || [];

  back.append(h('div', { class: 'modal' },
    h('h3', {}, o.orderNumber),
    h('p', { class: 'sub' }, h('span', { class: badgeClass(o.status) }, o.status),
      ` · paid with ${o.paymentMethod}`),

    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, 'Product'), h('th', { class: 'num' }, 'Unit'),
        h('th', { class: 'num' }, 'Qty'), h('th', { class: 'num' }, 'Total'))),
      h('tbody', {}, ...o.items.map((i) => h('tr', {},
        h('td', {}, i.productName),
        h('td', { class: 'num' }, money(i.unitPrice, o.currency)),
        h('td', { class: 'num' }, i.quantity),
        h('td', { class: 'num' }, money(i.totalPrice, o.currency))))))),

    h('div', { class: 'grid-2', style: 'margin-top:12px' },
      h('div', {}, h('label', {}, 'Subtotal'), h('div', {}, money(o.subtotal, o.currency))),
      h('div', {}, h('label', {}, 'Shipping'), h('div', {}, money(o.shippingFee, o.currency))),
      h('div', {}, h('label', {}, 'Tax'), h('div', {}, money(o.tax, o.currency))),
      h('div', {}, h('label', {}, 'Total'), h('div', {}, money(o.total, o.currency)))),

    o.shippingAddress
      ? h('div', {}, h('label', {}, 'Shipping address'),
          h('div', { class: 'hint' },
            `${o.shippingAddress.fullName}, ${o.shippingAddress.addressLine1}, ` +
            `${o.shippingAddress.city}, ${o.shippingAddress.country} · ${o.shippingAddress.phone}`))
      : null,

    h('div', { class: 'modal-actions' },
      h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'),
      ...next.map(([path, label]) =>
        h('button', {
          class: 'btn',
          onclick: () => {
            if (path === 'ship') {
              back.remove();
              formModal('Mark shipped', 'Adding a tracking number is optional.',
                [{ name: 'note', label: 'Tracking number' }],
                async (v) => {
                  await api(`/admin/store/orders/${o.id}/ship`, { method: 'POST', body: JSON.stringify({ note: v.note }) });
                  toast('Order marked shipped.', 'ok');
                  render();
                });
            } else {
              act(path, {}, label + '.');
            }
          },
        }, label)),

      ['Paid', 'Processing', 'Pending'].includes(o.status)
        ? h('button', {
            class: 'btn danger',
            onclick: () => {
              back.remove();
              confirmDialog('Cancel order',
                'Stock is returned and the money goes back to the wallet it came from.',
                (reason) => act('cancel', { note: reason }, 'Order cancelled and refunded.'),
                { danger: true, needsReason: true });
            },
          }, 'Cancel & refund')
        : null,

      ['Delivered', 'Completed'].includes(o.status)
        ? h('button', {
            class: 'btn danger',
            onclick: () => {
              back.remove();
              confirmDialog('Refund order',
                'A shopping-wallet purchase is refunded as shopping credit, never as cash.',
                async (reason) => {
                  try {
                    await api(`/admin/store/orders/${o.id}/refund`, {
                      method: 'POST', body: JSON.stringify({ reason }),
                    });
                    toast('Order refunded.', 'ok');
                    render();
                  } catch (e) { toast(e.message, 'err'); }
                }, { danger: true, needsReason: true });
            },
          }, 'Refund')
        : null)));

  document.body.append(back);
}

async function viewStoreSettings() {
  const s = await api('/admin/store/settings');

  const f = {};
  const bool = (name, label, value) => {
    const sel = h('select', {},
      h('option', { value: 'true', selected: value ? '' : null }, 'Enabled'),
      h('option', { value: 'false', selected: value ? null : '' }, 'Disabled'));
    f[name] = sel;
    return h('div', {}, h('label', {}, label), sel);
  };
  const numField = (name, label, value, step = '1') => {
    const input = h('input', { type: 'number', value, step });
    f[name] = input;
    return h('div', {}, h('label', {}, label), input);
  };

  const save = async () => {
    try {
      const body = {
        storeEnabled: f.storeEnabled.value === 'true',
        shoppingWalletEnabled: f.shoppingWalletEnabled.value === 'true',
        allowShoppingWalletPurchases: f.allowShopping.value === 'true',
        allowMainWalletPurchases: f.allowMain.value === 'true',
        defaultCurrency: s.defaultCurrency,
        taxEnabled: f.taxEnabled.value === 'true',
        taxPercentage: Number(f.taxPercentage.value),
        shippingEnabled: f.shippingEnabled.value === 'true',
        defaultShippingFee: Number(f.defaultShippingFee.value),
        minimumOrderAmount: Number(f.minimumOrderAmount.value),
        maximumOrderAmount: Number(f.maximumOrderAmount.value),
        shoppingCreditExpirationEnabled: f.creditExpiry.value === 'true',
        shoppingCreditExpirationDays: Number(f.creditExpiryDays.value),
        refundPolicy: s.refundPolicy,
        lowStockThreshold: Number(f.lowStockThreshold.value),
      };
      await api('/admin/store/settings', { method: 'PUT', body: JSON.stringify(body) });
      toast('Settings saved.', 'ok');
    } catch (e) { toast(e.message, 'err'); }
  };

  return h('div', {},
    pageHead('Store settings', 'تحكم في تشغيل المتجر وطرق الدفع والرسوم.'),
    h('div', { class: 'card' },
      h('h3', {}, 'Availability'),
      h('div', { class: 'grid-2' },
        bool('storeEnabled', 'Store', s.storeEnabled),
        bool('shoppingWalletEnabled', 'Shopping wallet', s.shoppingWalletEnabled),
        bool('allowShopping', 'Allow shopping wallet purchases', s.allowShoppingWalletPurchases),
        bool('allowMain', 'Allow main wallet purchases', s.allowMainWalletPurchases))),

    h('div', { class: 'card' },
      h('h3', {}, 'Pricing & shipping'),
      h('div', { class: 'grid-2' },
        bool('taxEnabled', 'Tax', s.taxEnabled),
        numField('taxPercentage', 'Tax %', s.taxPercentage, '0.01'),
        bool('shippingEnabled', 'Shipping', s.shippingEnabled),
        numField('defaultShippingFee', 'Default shipping fee', s.defaultShippingFee, '0.01'),
        numField('minimumOrderAmount', 'Minimum order', s.minimumOrderAmount, '0.01'),
        numField('maximumOrderAmount', 'Maximum order', s.maximumOrderAmount, '0.01'))),

    h('div', { class: 'card' },
      h('h3', {}, 'Shopping credit & stock'),
      h('div', { class: 'grid-2' },
        bool('creditExpiry', 'Credit expiration', s.shoppingCreditExpirationEnabled),
        numField('creditExpiryDays', 'Expires after (days)', s.shoppingCreditExpirationDays),
        numField('lowStockThreshold', 'Low stock threshold', s.lowStockThreshold)),
      h('div', { class: 'hint', style: 'margin-top:8px' },
        'Credit is only ever expired through an explicit ledger entry, so a balance always matches its history.')),

    h('div', { class: 'card' }, h('button', { class: 'btn', onclick: save }, 'Save settings')));
}

// ---------------------------------------------------------------- finance

async function viewWallets() {
  const q = state.cache.wallets || (state.cache.wallets = { page: 1, search: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);

  const data = await api(`/admin/wallets?${params}`);
  const search = h('input', { placeholder: 'Name or email', value: q.search });

  return h('div', {},
    pageHead('Wallets', 'Main and Shopping balances are always reported separately.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search,
        h('button', { class: 'btn ghost sm', onclick: () => { q.search = search.value; q.page = 1; render(); } }, 'Search')),

      pagedTable({
        columns: [
          { label: 'User', render: (w) => w.fullName },
          { label: 'Email', render: (w) => w.email || '—' },
          { label: 'Main (withdrawable)', num: true, render: (w) => money(w.mainBalance, w.currency) },
          { label: 'Shopping (store only)', num: true, render: (w) => money(w.shoppingBalance, w.currency) },
          { label: 'Auction refunds', num: true, render: (w) => money(w.totalAuctionRefunds, w.currency) },
          { label: 'Store spend', num: true, render: (w) => money(w.totalStorePurchases, w.currency) },
          { label: 'Last activity', render: (w) => when(w.lastTransactionAt) },
          { label: '', render: (w) => h('button', { class: 'btn ghost sm', onclick: () => openWallet(w) }, 'Open') },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function openWallet(w) {
  const tx = await api(`/admin/wallets/${w.userId}/transactions?pageSize=50`);
  const back = h('div', { class: 'modal-back' });

  const adjust = (walletType) => {
    back.remove();
    formModal(`تعديل محفظة ${tr(walletType)} — ${w.fullName}`,
      'Positive credits, negative debits. A reason is required and the change is audited.', [
        { name: 'amount', label: 'Amount', type: 'number', step: '0.01', value: 0 },
        { name: 'reason', label: 'Reason (required)' },
      ], async (v) => {
        await api(`/admin/users/${w.userId}/wallet-adjustment`, {
          method: 'POST',
          body: JSON.stringify({
            amount: Number(v.amount),
            currency: w.currency,
            walletType,
            reason: v.reason,
            idempotencyKey: `adj-${Date.now()}-${Math.random().toString(36).slice(2)}`,
          }),
        });
        toast('Wallet adjusted.', 'ok');
        render();
      });
  };

  back.append(h('div', { class: 'modal' },
    h('h3', {}, w.fullName),
    h('p', { class: 'sub' }, w.email || shortId(w.userId)),

    h('div', { class: 'wallet-split' },
      h('div', { class: 'wallet-box' },
        h('div', { class: 't' }, 'Main wallet'),
        h('div', { class: 'v' }, money(w.mainBalance, w.currency)),
        h('div', { class: 'n' }, 'Withdrawable'),
        h('button', { class: 'btn ghost sm', style: 'margin-top:10px', onclick: () => adjust('Main') }, 'Adjust')),
      h('div', { class: 'wallet-box' },
        h('div', { class: 't' }, 'Shopping credit'),
        h('div', { class: 'v' }, money(w.shoppingBalance, w.currency)),
        h('div', { class: 'n' }, 'Store purchases only · never withdrawable'),
        h('button', { class: 'btn ghost sm', style: 'margin-top:10px', onclick: () => adjust('Shopping') }, 'Issue credit'))),

    h('label', { style: 'margin-top:16px' }, 'Ledger'),
    h('div', { class: 'table-wrap' }, h('table', {},
      h('thead', {}, h('tr', {}, h('th', {}, 'When'), h('th', {}, 'Wallet'), h('th', {}, 'Type'),
        h('th', { class: 'num' }, 'Amount'), h('th', { class: 'num' }, 'Balance after'), h('th', {}, 'Description'))),
      h('tbody', {}, ...tx.items.map((t) => h('tr', {},
        h('td', {}, when(t.createdAt)),
        h('td', {}, h('span', { class: t.walletType === 'Shopping' ? 'badge info' : 'badge' }, t.walletType)),
        h('td', {}, t.transactionType),
        h('td', { class: 'num' }, money(t.amount, t.currency)),
        h('td', { class: 'num' }, money(t.balanceAfter, t.currency)),
        h('td', {}, t.description || '—')))))),

    h('div', { class: 'modal-actions' }, h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));

  document.body.append(back);
}

async function viewShoppingCredits() {
  const q = state.cache.credits || (state.cache.credits = { page: 1 });

  const [summary, data] = await Promise.all([
    api('/admin/shopping-credits/summary?currency=SAR'),
    api(`/admin/shopping-credits?page=${q.page}&pageSize=20`),
  ]);

  return h('div', {},
    pageHead('Shopping credits', 'The closed loop: entry fees in, store purchases out.'),

    h('div', { class: 'kpi-grid' },
      kpi('Issued', money(summary.totalIssued, summary.currency), true),
      kpi('Used', money(summary.totalUsed, summary.currency), true),
      kpi('Outstanding', money(summary.outstanding, summary.currency), true)),

    h('div', { class: 'card' },
      pagedTable({
        columns: [
          { label: 'User', render: (c) => c.fullName },
          { label: 'Email', render: (c) => c.email || '—' },
          { label: 'Issued', num: true, render: (c) => money(c.issued, c.currency) },
          { label: 'Used', num: true, render: (c) => money(c.used, c.currency) },
          { label: 'Remaining', num: true, render: (c) => money(c.remaining, c.currency) },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

// ---------------------------------------------------------------- system

/** +966501234567 → 050 123 4567, the way a Saudi mobile is written and read. Anything else as it is. */
function localPhone(value) {
  const m = /^\+9665(\d{8})$/.exec(value || '');
  return m ? `05${m[1].slice(0, 1)} ${m[1].slice(1, 4)} ${m[1].slice(4)}` : value;
}

async function viewUsers() {
  const q = state.cache.users || (state.cache.users = { page: 1, search: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 20 });
  if (q.search) params.set('search', q.search);

  const data = await api(`/admin/users?${params}`);
  const search = h('input', { placeholder: 'Name, email or phone', value: q.search });

  const setActive = (u) => confirmDialog(
    u.isActive ? 'Disable account' : 'Enable account',
    u.isActive ? 'Disabling revokes every active session.' : 'The user will be able to sign in again.',
    async (reason) => {
      try {
        await api(`/admin/users/${u.id}/active`, {
          method: 'PUT', body: JSON.stringify({ isActive: !u.isActive, reason }),
        });
        toast('User updated.', 'ok');
        render();
      } catch (e) { toast(e.message, 'err'); }
    }, { danger: u.isActive, needsReason: true });

  const editRoles = (u) => formModal(`الصلاحيات — ${u.fullName}`,
    'Comma-separated. Only SUPER_ADMIN may change roles.', [
      { name: 'roles', label: 'Roles', value: u.roles.join(', ') },
      { name: 'reason', label: 'Reason (required)' },
    ], async (v) => {
      await api(`/admin/users/${u.id}/roles`, {
        method: 'PUT',
        body: JSON.stringify({
          roles: v.roles.split(',').map((r) => r.trim()).filter(Boolean),
          reason: v.reason,
        }),
      });
      toast('Roles updated.', 'ok');
      render();
    });

  return h('div', {},
    pageHead('Users', 'الحسابات وصلاحياتها وأرصدتها.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search,
        h('button', { class: 'btn ghost sm', onclick: () => { q.search = search.value; q.page = 1; render(); } }, 'Search')),

      pagedTable({
        columns: [
          // An account made by signing in with a phone has no name until its owner gives one, and
          // no email at all; the dash says so rather than leaving an empty cell to be puzzled over.
          { label: 'Name', render: (u) => u.fullName || h('span', { class: 'muted' }, '—') },
          {
            label: 'الهاتف',
            mono: true,
            // Shown as people write it in Saudi Arabia, left to right whatever the page direction.
            render: (u) => u.phoneNumber
              ? h('span', { dir: 'ltr' }, localPhone(u.phoneNumber))
              : h('span', { class: 'muted' }, '—'),
          },
          { label: 'Email', render: (u) => u.email || h('span', { class: 'muted' }, '—') },
          { label: 'Roles', render: (u) => h('div', { class: 'row' }, ...u.roles.map((r) => h('span', { class: 'badge' }, r))) },
          { label: 'Main', num: true, render: (u) => money(u.mainWalletBalance, u.currency) },
          { label: 'Shopping', num: true, render: (u) => money(u.shoppingWalletBalance, u.currency) },
          { label: 'Status', render: (u) => h('span', { class: u.isActive ? 'badge ok' : 'badge danger' }, u.isActive ? 'Active' : 'Disabled') },
          { label: 'Last login', render: (u) => when(u.lastLoginAt) },
          {
            label: '',
            render: (u) => h('div', { class: 'row' },
              h('button', { class: 'btn ghost sm', onclick: () => editRoles(u) }, 'Roles'),
              h('button', { class: 'btn ghost sm', onclick: () => setActive(u) }, u.isActive ? 'Disable' : 'Enable')),
          },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

async function viewAudit() {
  const q = state.cache.audit || (state.cache.audit = { page: 1, search: '' });

  const params = new URLSearchParams({ page: q.page, pageSize: 30 });
  if (q.search) params.set('search', q.search);

  const data = await api(`/admin/audit-logs?${params}`);
  const search = h('input', { placeholder: 'Actor email or reason', value: q.search });

  return h('div', {},
    pageHead('Audit log', 'Append-only record of every administrative and financial action.'),
    h('div', { class: 'card' },
      h('div', { class: 'toolbar' }, search,
        h('button', { class: 'btn ghost sm', onclick: () => { q.search = search.value; q.page = 1; render(); } }, 'Search')),

      pagedTable({
        columns: [
          { label: 'When', render: (a) => when(a.createdAt) },
          { label: 'Action', render: (a) => h('span', { class: 'badge' }, a.action) },
          { label: 'Actor', render: (a) => a.actorEmail || 'system' },
          { label: 'Entity', render: (a) => `${a.entityType} ${shortId(a.entityId)}` },
          { label: 'Reason', render: (a) => a.reason || '—' },
          { label: 'IP', mono: true, render: (a) => a.ipAddress || '—' },
          {
            label: 'Change',
            render: (a) => h('button', {
              class: 'btn ghost sm',
              onclick: () => {
                const back = h('div', { class: 'modal-back' });
                back.append(h('div', { class: 'modal' },
                  h('h3', {}, a.action),
                  h('p', { class: 'sub' }, when(a.createdAt)),
                  h('label', {}, 'Before'),
                  h('pre', { class: 'mono', style: 'white-space:pre-wrap' }, a.oldValue || '—'),
                  h('label', {}, 'After'),
                  h('pre', { class: 'mono', style: 'white-space:pre-wrap' }, a.newValue || '—'),
                  h('div', { class: 'modal-actions' },
                    h('button', { class: 'btn ghost', onclick: () => back.remove() }, 'Close'))));
                document.body.append(back);
              },
            }, 'View'),
          },
        ],
        rows: data.items,
        page: data.page,
        totalPages: data.totalPages,
        totalCount: data.totalCount,
        onPage: (p) => { q.page = p; render(); },
      })));
}

// ---------------------------------------------------------------- bootstrap

function render() {
  const app = document.getElementById('app');
  app.replaceChildren(state.auth ? renderShell() : renderLogin());
}

state.auth = store.load();
render();

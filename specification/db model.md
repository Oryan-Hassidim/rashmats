# מודל נתונים (Data Model & Schema) - Rashmatz

ארכיטקטורת נתונים רלציונית אחודה, יעילה ומודרנית. מותאמת ב-100% להרצה הן על **PostgreSQL** והן על **SQLite** ללא צורך בשינויים מהותיים בסכמה.

## 1. עקרונות תאימות וייעול (Cross-DB Standards)

1. **מזהים (Primary Keys):** שימוש ב-UUID (או UUIDv7 ממוין זמן). ב-PostgreSQL הטיפוס הוא `UUID`, וב-SQLite הטיפוס הוא `TEXT`. מזהים אלו מונעים חשיפת נתונים ומאפשרים יצירת מזהה אופליין בצד הלקוח.
2. **מחרוזות:** שימוש בטיפוס `TEXT` סטנדרטי (ב-PostgreSQL וב-SQLite אין יתרון ל-`VARCHAR(n)`).
3. **סטטוסים ו-Enums קומפקטיים:** מנוהלים כמספרים שלמים קטנים (`SMALLINT` ב-Postgres / `INTEGER` ב-SQLite) התופסים 1–2 בתים בלבד.
4. **ישות מחזיק אחודה (`custody_holders`):** ביטול ההפרדה בין אדם למיקום. כל גורם שיכול להחזיק ציוד (עובד, קבלן, מחסן, רכב, מדף) מיוצג בישות אחת.
5. **הבחנה בין משמורת (Custody) למיקום פיזי (Location - עקרון OR גמיש):**
   * **משמורת:** מגדירה את האחריות החוקית/ניהולית על הפריט (`current_holder_id`).
   * **מיקום פיזי:** ניתן להגדרה כישות רשומה במערכת (`location_holder_id`), כטקסט חופשי (`location_custom_text`), או **כשניהם יחד** (למשל: ישות "מחסן ראשי" + פירוט טקסטואלי "ארונית 3, מגירה ב'").
   * כל עדכון מיקום ללא העברת בעלות מתועד ביומן ייעודי בלתי-מחיק.
6. **תבניות העברה מוכנות (Transfer Templates / Kits):** מאפשרות הגדרת "ערכות ציוד" או העברות קבועות מראש (למשל: ערכת קליטת עובד, ערכת יציאה לשטח), עם מקור/יעד אופציונליים ורשימת מוצרים וכמויות. בעת ביצוע העברה בפועל מתוך תבנית, עבור פריטים סריאליים נבחרים או נסרקים הפריטים הספציפיים.
7. **מעקב שינויים (`updated_at` מול Append-only):** מנועי SQL אינם מנהלים היסטוריית שורה אוטומטית. לכן, טבלאות מצב מתעדכנות (Mutable Entities) מכילות עמודת `updated_at`. טבלאות יומן בלתי-מחיקות (Append-only / Immutable logs, כגון העברות בעלות ותנועות מיקום) אינן כוללות שדה זה, אלא רק חותמת מועד יצירה.
8. **אינדוקס ממוקד (Lean Indexing):** הסרת אינדקסים מיותרים על עמודות בעלות שונות נמוכה (Low Cardinality כגון סטטוסים בודדים). השארת אינדקסים רק עבור FK פעילים ב-JOINs ואינדקסי `UNIQUE` עסקיים.

---

## 2. דיאגרמת קשרים כללית (Unified High-Level ERD)

```
User (Global Google Auth)
  └── Organization (Tenant)
        ├── Department (Organizational units / Teams)
        ├── CustodyHolder (Person / Warehouse / Vehicle / Shelf)
        │     ├── HolderCertification (Skills & Permits)
        │     ├── SerializedItem (Items currently held / located)
        │     └── InventoryStock (Bulk stock balances)
        ├── Certification (Catalog of skills/trainings)
        ├── ItemCategory (Catalog categories)
        ├── ItemType (Catalog of equipment definitions)
        ├── TransferTemplate (Predefined kits/transfer presets)
        │     └── TransferTemplateLine (Item type & quantity)
        ├── CustodyTransfer (Transfer header: Inbound / Internal / Outbound)
        │     └── CustodyTransferLine (Specific items & quantities transferred)
        ├── SerializedItemLocationLog (Append-only physical location movement log)
        └── AuditSession
              └── AuditItemRecord (Pre-populated checklist & scan execution)
```

---

## 3. ניהול משתמשים וארגונים (Identity & Tenancy)

### `users` (חשבונות משתמש גלובליים מגוגל)
משתמשים מאומתים דרך Google OAuth. אינם שייכים ישירות לארגון בודד.

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה ייחודי גלובלי |
| `google_id` | `TEXT` | `UNIQUE, NOT NULL` | מזהה משתמש קבוע מגוגל (`sub`) |
| `email` | `TEXT` | `UNIQUE, NOT NULL` | כתובת דוא"ל (Lowercased) |
| `full_name` | `TEXT` | `NOT NULL` | שם מלא כפי שמוגדר בחשבון גוגל |
| `avatar_url` | `TEXT` | `NULLABLE` | תמונת פרופיל מגוגל |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד יצירה ראשוני |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון אחרון |

*אינדקסים:*
* `CREATE UNIQUE INDEX idx_users_google_id ON users(google_id);`
* `CREATE UNIQUE INDEX idx_users_email ON users(email);`

### `organizations` (שוכרים / ארגונים - Tenants)

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה ארגון |
| `name` | `TEXT` | `NOT NULL` | שם הארגון |
| `slug` | `TEXT` | `UNIQUE, NOT NULL` | מזהה טקסטואלי עבור נתיבים וגישה |
| `is_active` | `INTEGER` | `NOT NULL, DEFAULT 1` | `1` = פעיל, `0` = מושבת |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד הקמת הארגון |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון הגדרות הארגון |

*אינדקסים:*
* `CREATE UNIQUE INDEX idx_organizations_slug ON organizations(slug);`

### `departments` (מחלקות / אגפים / יחידות בארגון)
מבנה ארגוני פנימי עבור שיוך עובדים, צוותים ומחסנים ייעודיים.

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה מחלקה ייחודי |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך לארגון |
| `name` | `TEXT` | `NOT NULL` | שם המחלקה / צוות |
| `code` | `TEXT` | `NULLABLE` | קוד זיהוי קצר (למשל: "OPS", "LOG") |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד הקמה |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון פרטים |

*אינדקסים:*
* `CREATE UNIQUE INDEX idx_departments_org_name ON departments(organization_id, name);`

---

## 4. ישות מחזיק אחודה, נוכחות והכשרות (Custodians, Attendance & Certifications)

### `custody_holders` (מחזיק ציוד - אדם או מיקום)
טבלה מאוחדת המרכזת אנשים ומקומות. משמשת גם כ-Whitelist המורשים לארגון וכמעקב נוכחות לאנשי הארגון.

*מיפוי סוג מחזיק (`holder_type`):*
* `0` = מיקום (מחסן, מכולה, רכב, מדף)
* `1` = אדם / עובד / חבר ארגון

*מיפוי תפקיד הרשאה (`role`):*
* `0` = ללא גישה למערכת (רלוונטי למיקומים או לעובדים ללא ממשק)
* `1` = MEMBER (משתמש קצה: צפייה בציוד שלו, ביצוע סריקות בביקורת)
* `2` = ADMIN (מנהל מערכת: העברות, ביקורות, ניהול פריטים והרשאות)

*מיפוי סטטוס נוכחות (`attendance_status` - רלוונטי לאנשים):*
* `0` = לא פעיל / לא זמין (`INACTIVE`)
* `1` = נוכח פיזית בארגון (`PRESENT`)
* `2` = בבית / מרחוק (`HOME / REMOTE`)

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה מחזיק ייחודי |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך ארגוני |
| `department_id` | `UUID / TEXT` | `NULLABLE, REFERENCES departments(id)` | שיוך למחלקה / צוות בארגון |
| `user_id` | `UUID / TEXT` | `NULLABLE, REFERENCES users(id)` | קישור לחשבון גוגל מאומת (אם מדובר באדם) |
| `holder_type` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1` | `0` = מיקום, `1` = אדם |
| `name` | `TEXT` | `NOT NULL` | שם האדם או המיקום |
| `internal_identifier` | `TEXT` | `NULLABLE` | מזהה פנימי (מספר עובד, קוד מחסן) |
| `invited_email` | `TEXT` | `NULLABLE` | מייל ל-Whitelist (עבור אנשים) |
| `role` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | רמת הרשאה בארגון (`0` = ללא, `1` = חבר, `2` = מנהל) |
| `attendance_status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | סטטוס נוכחות (`0`=לא פעיל, `1`=נוכח, `2`=בבית) |
| `phone_number` | `TEXT` | `NULLABLE` | טלפון ליצירת קשר |
| `is_active` | `INTEGER` | `NOT NULL, DEFAULT 1` | `1` = פעיל, `0` = מושבת |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד הוספה |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון אחרון |

*אינדקסים ואילוצים:*
* `CREATE UNIQUE INDEX idx_custody_holders_org_internal ON custody_holders(organization_id, internal_identifier) WHERE internal_identifier IS NOT NULL;`
* `CREATE UNIQUE INDEX idx_custody_holders_org_user ON custody_holders(organization_id, user_id) WHERE user_id IS NOT NULL;`
* `CREATE INDEX idx_custody_holders_org_email ON custody_holders(organization_id, invited_email);`
* `CREATE INDEX idx_custody_holders_department ON custody_holders(department_id);`

### `certifications` (קטלוג הכשרות והרשאות)
סוגי הכשרות שהארגון מגדיר (למשל: "נהיגה במלגזה", "מפעיל קשר", "עבודה בגובה").

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה הכשרה |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך ארגוני |
| `name` | `TEXT` | `NOT NULL` | שם ההכשרה |
| `description` | `TEXT` | `NULLABLE` | תיאור / דרישות קדם |
| `validity_months` | `INTEGER` | `NULLABLE` | תוקף בחודשים (אם נדרש חידוש תקופתי) |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד הוספה |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון הגדרת הכשרה |

*אינדקסים:*
* `CREATE UNIQUE INDEX idx_certifications_org_name ON certifications(organization_id, name);`

### `holder_certifications` (הכשרות למחזיקים)
טבלת קשר המייצגת תעודות פעילות. רשומה עשויה להתעדכן בעת הארכת תוקף.

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה רשומה |
| `holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | מזהה האדם המחזיק בהכשרה |
| `certification_id` | `UUID / TEXT` | `NOT NULL, REFERENCES certifications(id)` | מזהה ההכשרה |
| `acquired_at` | `TIMESTAMPTZ / TEXT` | `NOT NULL` | מועד קבלת ההכשרה |
| `expires_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | תאריך תפוגה (אם יש) |
| `certificate_number` | `TEXT` | `NULLABLE` | מספר תעודה / רישיון |
| `notes` | `TEXT` | `NULLABLE` | הערות |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון פרטי תעודה |

*אינדקסים:*
* `CREATE INDEX idx_holder_cert_holder ON holder_certifications(holder_id);`
* `CREATE INDEX idx_holder_cert_cert ON holder_certifications(certification_id);`

---

## 5. קטלוג ציוד ומלאי (Equipment Catalog & Inventory)

### `item_categories` (קטגוריות ציוד)
קטלוג קטגוריות מנוהל ברמת הארגון למניעת שגיאות כתיב וכפילויות (למשל: "מחשוב", "קשר", "כלי עבודה", "ציוד מגן").

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה קטגוריה |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך ארגוני |
| `name` | `TEXT` | `NOT NULL` | שם הקטגוריה |
| `description` | `TEXT` | `NULLABLE` | תיאור |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד יצירה |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון שם/תיאור קטגוריה |

*אינדקסים:*
* `CREATE UNIQUE INDEX idx_item_categories_org_name ON item_categories(organization_id, name);`

### `item_types` (סוגי פריטים בקטלוג)

*מיפוי אופן מעקב (`tracking_type`):*
* `0` = כמותי (ספירת מלאי פשוטה)
* `1` = סריאלי (ניהול פריט בודד עם מספר סידורי וברקוד)

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה סוג פריט |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך ארגוני |
| `category_id` | `UUID / TEXT` | `NULLABLE, REFERENCES item_categories(id)` | שיוך לקטגוריה מנוהלת |
| `name` | `TEXT` | `NOT NULL` | שם הדגם / פריט |
| `tracking_type` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 1` | `0` = כמותי, `1` = סריאלי |
| `unit_of_measure` | `TEXT` | `DEFAULT 'unit'` | יחידת מידה |
| `description` | `TEXT` | `NULLABLE` | מפרט והערות |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד יצירה |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון מפרט פריט |

*אינדקסים:*
* `CREATE INDEX idx_item_types_org ON item_types(organization_id);`
* `CREATE INDEX idx_item_types_category ON item_types(category_id);`

### `serialized_items` (ציוד סריאלי ייחודי)
ישות מצב דינמית המשלבת משמורת חוקית (`current_holder_id`) לצד מיקום פיזי.
המיקום הפיזי גמיש (עקרון OR): ניתן לציין ישות רשומה (`location_holder_id`), טקסט חופשי (`location_custom_text`), או את שניהם יחד (למשל: ישות "מחסן ב'" + טקסט חופשי "ארונית 4, מדף עליון").

*מיפוי סטטוס פריט (`status`):*
* `0` = פעיל ותקין (`ACTIVE`)
* `1` = בטיפול / תקול (`MAINTENANCE`)
* `2` = אבוד / חסר (`LOST`)
* `3` = נגרט / יצא משימוש (`DECOMMISSIONED`)

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה פריט ייחודי |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך לארגון |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id)` | סוג הפריט בקטלוג |
| `serial_number` | `TEXT` | `NULLABLE` | מספר סידורי של היצרן |
| `barcode` | `TEXT` | `NOT NULL` | ערך ברקוד / QR לסריקה פיזית |
| `current_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | המחזיק הרשמי האחראי (אדם או מחסן ראשי) |
| `location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | מיקום פיזי כישות רשומה (מחסן, חדר, רכב) |
| `location_custom_text` | `TEXT` | `NULLABLE` | מיקום פיזי כטקסט חופשי (פירוט מיקום ספציפי) |
| `status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | סטטוס מצב הפריט |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד קליטה במערכת |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון סטטוס/מחזיק/מיקום |

*אינדקסים ואילוצים ייחודיים:*
* `CREATE UNIQUE INDEX idx_serialized_org_barcode ON serialized_items(organization_id, barcode);`
* `CREATE UNIQUE INDEX idx_serialized_org_serial ON serialized_items(organization_id, serial_number) WHERE serial_number IS NOT NULL;`
* `CREATE INDEX idx_serialized_current_holder ON serialized_items(current_holder_id);`
* `CREATE INDEX idx_serialized_location_holder ON serialized_items(location_holder_id);`
* `CREATE INDEX idx_serialized_item_type ON serialized_items(item_type_id);`

### `inventory_stocks` (מלאי כמותי)
ריכוז כמויות דינמי עבור פריטים כמותיים (`tracking_type = 0`).

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה שורת מלאי |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך לארגון |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id)` | שיוך לקטלוג |
| `holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | המחזיק במלאי (מחסן או אדם) |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 0` | כמות במלאי |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד שינוי מאזן כמויות אחרון |

*אינדקסים ואילוצים:*
* `CREATE UNIQUE INDEX idx_inventory_stocks_holder_item ON inventory_stocks(holder_id, item_type_id);`
* `CREATE INDEX idx_inventory_stocks_org ON inventory_stocks(organization_id);`

---

## 6. תבניות העברה מוכנות (Transfer Templates / Kits)

תבניות המאפשרות הגדרת "ערכות ציוד" או העברות חוזרות קבועות (למשל: "ערכת קליטת עובד חדש", "ערכת יציאה לפעילות שטח"). 
התבנית מגדירה מוצרים מבוקשים וכמויות. בעת הפעלת התבנית להעברה אמיתית, המנהל בוחר או סורק את הפריטים הסריאליים הספציפיים שיועברו בפועל.

### `transfer_templates` (כותרת תבנית העברה)

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה תבנית |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך לארגון |
| `name` | `TEXT` | `NOT NULL` | שם התבנית (למשל: "סט ציוד מנהל עבודה") |
| `description` | `TEXT` | `NULLABLE` | תיאור התבנית ושימושיה |
| `default_source_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | מקור ברירת מחדל אופציונלי (למשל מחסן ראשי) |
| `default_dest_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | יעד ברירת מחדל אופציונלי |
| `is_active` | `INTEGER` | `NOT NULL, DEFAULT 1` | `1` = פעיל, `0` = לא פעיל |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד יצירה |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון הגדרות התבנית |

*אינדקסים:*
* `CREATE UNIQUE INDEX idx_transfer_templates_org_name ON transfer_templates(organization_id, name);`

### `transfer_template_lines` (שורות פריטים בתבנית)

מפתח ראשי מורכב: `PRIMARY KEY (template_id, line_number)`.

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `template_id` | `UUID / TEXT` | `NOT NULL, REFERENCES transfer_templates(id) ON DELETE CASCADE` | מזהה התבנית הראשית |
| `line_number` | `INTEGER` | `NOT NULL` | מספר שורה רץ בתוך התבנית (1, 2, 3...) |
| `item_type_id` | `UUID / TEXT` | `NOT NULL, REFERENCES item_types(id)` | סוג המוצר בקטלוג |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 1` | כמות נדרשת מהמוצר (לסריאלי או כמותי) |

*אינדקסים:*
* האינדקס הראשי נוצר אוטומטית על `(template_id, line_number)`.
* `CREATE INDEX idx_template_lines_item_type ON transfer_template_lines(item_type_id);`

---

## 7. יומני תנועה בלתי ניתנים לשינוי (Immutable Activity Logs)

כל שינוי משמורת (בעלות) או שינוי מיקום פיזי נשמרים כרשומות Append-only בלבד. שורות אלו אינן כוללות `updated_at`.

### `custody_transfers` (כותרת העברת משמורת/בעלות - Transfer Header)

* אילוץ עסקי קשיח: כאשר `source_holder_id` או `dest_holder_id` שווים ל-`NULL`, שדה `notes` **חייב להכיל ערך** (לא ריק).
* אילוץ `CHECK`: `CHECK ((source_holder_id IS NOT NULL AND dest_holder_id IS NOT NULL) OR (notes IS NOT NULL AND TRIM(notes) != ''))`

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה פנימי של ההעברה |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך לארגון |
| `transfer_template_id` | `UUID / TEXT` | `NULLABLE, REFERENCES transfer_templates(id)` | שיוך לתבנית אם ההעברה נוצרה על בסיסה |
| `transfer_number` | `TEXT` | `NOT NULL` | מספר אסמכתא ייחודי בארגון (למשל: "TR-2026-00042") |
| `admin_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | המנהל שביצע ואישר את ההעברה |
| `source_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | מחזיק מקור (`NULL` = קליטה חיצונית) |
| `dest_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | מחזיק יעד (`NULL` = גריטה / השאלה חיצונית) |
| `notes` | `TEXT` | `NULLABLE` | חובה אם המקור או היעד הם `NULL` |
| `transferred_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד ביצוע ההעברה הסופי |

*אינדקסים ואילוצים:*
* `CREATE UNIQUE INDEX idx_transfers_org_number ON custody_transfers(organization_id, transfer_number);`
* `CREATE INDEX idx_transfers_source ON custody_transfers(source_holder_id);`
* `CREATE INDEX idx_transfers_dest ON custody_transfers(dest_holder_id);`
* `CREATE INDEX idx_transfers_org_date ON custody_transfers(organization_id, transferred_at DESC);`

### `custody_transfer_lines` (שורות פריטים בהעברת משמורת - Transfer Lines)

טבלה תלויה בהעברת האב (Weak Entity).
* מפתח ראשי מורכב: `PRIMARY KEY (transfer_id, line_number)`.
* אילוץ `CHECK`: בדיוק אחד משני השדות מוגדר: `(serialized_item_id IS NOT NULL AND item_type_id IS NULL) OR (serialized_item_id IS NULL AND item_type_id IS NOT NULL)`
* אילוץ `CHECK`: עבור פריט סריאלי `quantity = 1`.

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `transfer_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_transfers(id) ON DELETE CASCADE` | מזהה ההעברה הראשית (חלק מהמפתח הראשי) |
| `line_number` | `INTEGER` | `NOT NULL` | מספר שורה רץ בתוך ההעברה (1, 2, 3...) |
| `serialized_item_id` | `UUID / TEXT` | `NULLABLE, REFERENCES serialized_items(id)` | פריט סריאלי ספציפי (אם רלוונטי) |
| `item_type_id` | `UUID / TEXT` | `NULLABLE, REFERENCES item_types(id)` | סוג פריט כמותי (אם רלוונטי) |
| `quantity` | `INTEGER` | `NOT NULL, DEFAULT 1` | כמות שהועברה (לסריאלי: תמיד 1) |

*אינדקסים:*
* האינדקס הראשי נוצר אוטומטית על `(transfer_id, line_number)`.
* `CREATE INDEX idx_transfer_lines_serialized_item ON custody_transfer_lines(serialized_item_id);`

### `serialized_item_location_logs` (יומן שינויי מיקום פיזי בלבד - Location Audit Log)

מתעד שינוי במיקום הפיזי של מוצר סריאלי ללא שינוי בעלות/משמורת (למשל: הזזה ממדף א' למדף ב', העברה לחדר בדיקות, או השאלה רגעית).
* אילוץ `CHECK` ליעד: לפחות אחד מהשדות (ישות מיקום או טקסט) חייב להיות מוגדר:
  `CHECK (to_location_holder_id IS NOT NULL OR to_location_custom_text IS NOT NULL)`

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה ייחודי של רישום שינוי המיקום |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך לארגון |
| `serialized_item_id` | `UUID / TEXT` | `NOT NULL, REFERENCES serialized_items(id)` | הפריט שהוזז |
| `admin_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | המשתמש/מנהל שעדכן את המיקום |
| `from_location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | מיקום ישות קודם |
| `from_location_custom_text` | `TEXT` | `NULLABLE` | מיקום טקסט חופשי קודם |
| `to_location_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | מיקום ישות חדש (אם נבחר מחסן/חדר רשום) |
| `to_location_custom_text` | `TEXT` | `NULLABLE` | מיקום טקסט חופשי חדש |
| `notes` | `TEXT` | `NULLABLE` | סיבת הזזה / הערות נוספות |
| `recorded_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | חותמת זמן מדויקת של שינוי המיקום |

*אינדקסים:*
* `CREATE INDEX idx_location_logs_item ON serialized_item_location_logs(serialized_item_id);`
* `CREATE INDEX idx_location_logs_org_date ON serialized_item_location_logs(organization_id, recorded_at);`

---

## 8. ביקורות שטח ורשימת תיוג לסריקות (Field Audits & Item Records)

מודל הביקורת מבוסס על **רשימת תיוג שנוצרת מראש (Pre-populated Checklist)** בעת פתיחת הסבב:
1. בעת פתיחת סבב ביקורת (עבור כלל הארגון או ממוקד למחלקה/מחזיקים), המערכת מייצרת רשומות ב-`audit_item_records` עבור כל הפריטים הסריאליים הנדרשים לבדיקה בסבב זה, עם סטטוס התחלתי `0` (ממתין לסריקה).
2. בכל רגע נתון, התקדמות הביקורת מחושבת ישירות: `(נסרקו / סך כל הרשומות בסבב) * 100`.
3. כאשר משתמש סורק פריט:
   * אם הפריט קיים בסבב: הרשומה מתעדכנת לסטטוס `1` (נסרק תקין) או `2` (חריגה במחזיק), בצירוף מזהה הסורק ושעת הסריקה.
   * אם נסרק פריט שלא היה מוגדר ברשימת הבדיקה המקורית: נוצרת שורה עם סטטוס `3` (פריט בלתי צפוי שנסרק).

### `audit_sessions` (סבבי ביקורת)

*מיפוי סטטוס ביקורת (`status`):*
* `0` = פתוחה לסריקות (`OPEN`)
* `1` = סגורה (`CLOSED`)
* `2` = בארכיון (`ARCHIVED`)

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה סבב ביקורת |
| `organization_id` | `UUID / TEXT` | `NOT NULL, REFERENCES organizations(id)` | שיוך לארגון |
| `title` | `TEXT` | `NOT NULL` | כותרת הביקורת (למשל: "מסדר ציוד רבעוני מחלקת מבצעים") |
| `initiated_by_holder_id` | `UUID / TEXT` | `NOT NULL, REFERENCES custody_holders(id)` | המנהל שפתח את הביקורת |
| `status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | סטטוס הביקורת |
| `opened_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד פתיחה |
| `closed_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | מועד סגירה |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון סטטוס/כותרת הביקורת |

*אינדקסים:*
* `CREATE INDEX idx_audit_sessions_org ON audit_sessions(organization_id);`

### `audit_item_records` (רשומות מעקב סריקה לכל פריט בסבב)

*מיפוי סטטוס סריקה (`scan_status`):*
* `0` = ממתין לסריקה (`PENDING`) - נוצר בעת פתיחת הביקורת, טרם נסרק
* `1` = נסרק תקין (`VERIFIED`) - נסרק על ידי המחזיק הרשום הצפוי
* `2` = חריגה במחזיק (`MISMATCH`) - נסרק אצל מישהו שאינו המחזיק הרשום
* `3` = פריט לא צפוי (`UNEXPECTED`) - פריט שלא היה מיועד להיסקר בסבב זה ונסרק בפועל

| שדה | טיפוס | אילוצים / ברירת מחדל | תיאור |
| :--- | :--- | :--- | :--- |
| `id` | `UUID / TEXT` | `PRIMARY KEY` | מזהה ייחודי של שורת המעקב |
| `audit_session_id` | `UUID / TEXT` | `NOT NULL, REFERENCES audit_sessions(id) ON DELETE CASCADE` | שיוך לסבב ביקורת ספציפי |
| `serialized_item_id` | `UUID / TEXT` | `NOT NULL, REFERENCES serialized_items(id)` | הפריט הסריאלי שמבוקר |
| `expected_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | המחזיק הרשום במערכת בעת פתיחת הסבב |
| `scan_status` | `SMALLINT / INTEGER` | `NOT NULL, DEFAULT 0` | סטטוס מעקב נוכחי (`0`=ממתין, `1`=תקין, `2`=חריגה, `3`=לא צפוי) |
| `scanned_by_holder_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | האדם שביצע את הסריקה בפועל |
| `scanned_at` | `TIMESTAMPTZ / TEXT` | `NULLABLE` | חותמת זמן מדויקת שבה הפריט נסרק פיזית |
| `resolved_by_admin_id` | `UUID / TEXT` | `NULLABLE, REFERENCES custody_holders(id)` | מנהל שאישר/פתר חריגה (אם הייתה כזו) |
| `resolution_notes` | `TEXT` | `NULLABLE` | נימוק או פעולה שננקטה בעקבות חריגה |
| `created_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד יצירת הרשומה (פתיחת הביקורת) |
| `updated_at` | `TIMESTAMPTZ / TEXT` | `DEFAULT CURRENT_TIMESTAMP` | מועד עדכון אחרון (סריקה / פתרון חריגה) |

*אינדקסים ואילוצים:*
* `CREATE UNIQUE INDEX idx_audit_item_session_item ON audit_item_records(audit_session_id, serialized_item_id);`
* `CREATE INDEX idx_audit_item_session_status ON audit_item_records(audit_session_id, scan_status);`
* `CREATE INDEX idx_audit_item_holder ON audit_item_records(scanned_by_holder_id);`
/**
 * @OnlyCurrentDoc
 */


function test() {
    editRows('כוח אדם', [["5", { "מזהה": "'5555555", "שם פרטי": "טל", "שם משפחה": "אלזמי", "מספר נייד": "'0542301338", "מזהה מסגרת": 3, "תפקיד": "רס\"פ", "הסמכות": "", "סטטוס": "" }]]);
}

const ID_STRING = 'מזהה';
const PAGES = [
    { name: 'דאשבוארד', id: 'dashboard' },
    { name: 'מסגרות', id: 'frames' },
    { name: 'כוח אדם', id: 'personnel' },
    { name: 'צל"ם', id: 'zelem' },
    { name: 'רישום העברה', id: 'handover' },
    { name: 'היסטוריה', id: 'history' },
    { name: 'סטים להחתמה', id: 'presets' },
];

/*

*Scheme*:

*הרשאות*:
- דוא"ל
- מספר אישי

*מסגרות*:
- מזהה
- שם
- מזהה מפקד

*כוח אדם*:
- מזהה
- שם פרטי
- שם משפחה
- מספר נייד
- מזהה מסגרת
- תפקיד
- הסמכות
- סטטוס
- חולצת ב'※ציוד ב'※160
- מכנסי ב'※ציוד ב'※160
- כובע ב'※ציוד ב'※80
- חגורה※ציוד ב'※80
- שק"ש※ציוד ב'※100
- ברכיות※תרומה※80

*צל"ם*:
- מזהה
- סוג
- שיוך
- מיקום
- קטגוריה
- שינוי אחרון

*היסטוריה*:
- תאריך ושעה
- מזהה מבצע
- מזהה נותן
- מזהה מקבל
- שם מוצר / צל"ם
- כמות
- כמות נותן אחרי
- כמות מקבל אחרי
- סיבה
- הערות

*סט החתמה*:
- מזהה
- סט

*/

function getScheme() {
    const sheets = SpreadsheetApp.getActive().getSheets();
    const scheme = {};
    var message =
        sheets.map(sheet => {
            const name = sheet.getName();
            const headers = sheet.getRange(1, 1, 1, sheet.getLastColumn()).getValues()[0];
            scheme[name] = headers;
            return `*${name}*:\n${headers.map(h => `- ${h}`).join('\n')}`;
        }).join('\n\n');
    Logger.log(message);
    return scheme;
}

function getData(tableName) {
    return SpreadsheetApp.getActive().getSheetByName(tableName).getDataRange().getValues();
}

function getDataById(tableName, id) {
    const data = getData(tableName);
    return data.find(row => String(row[0]) === String(id));
}

function appendRow(tableName, row) {
    const lock = LockService.getDocumentLock();
    try {
        lock.waitLock(30000);
        const sheet = SpreadsheetApp.getActive().getSheetByName(tableName);
        sheet.appendRow(row);
        return true;
    } catch (e) {
        Logger.log('Could not obtain lock: ' + e);
        return false;
    }
    finally {
        lock.releaseLock();
    }
}

function editRows(tableName, updates) {
    // updates = [[3, {מזהה: 1, שם: 'new name', מזהה מפקד: 2}], ...]
    Logger.log(`Editing rows in table ${tableName} with updates: ${JSON.stringify(updates)}`);
    const lock = LockService.getDocumentLock();
    try {
        lock.waitLock(30000);
        const sheet = SpreadsheetApp.getActive().getSheetByName(tableName);
        const data = sheet.getDataRange().getValues();
        const headers = data[0];
        updates.forEach(update => {
            // remove "'" from id starting if exists
            var updateId = update[0];
            if (typeof updateId === 'string' && updateId.startsWith("'")) {
                updateId = updateId.substring(1);
            }
            const rowIndex = data.findIndex(row => String(row[0]) === String(updateId));
            if (rowIndex === -1) {
                throw new Error(`Row with id ${updateId} not found in table ${tableName}`);
            }
            const row = data[rowIndex];
            for (const key in update[1]) {
                const colIndex = headers.indexOf(key);
                if (colIndex === -1) {
                    throw new Error(`Column ${key} not found in table ${tableName}`);
                }
                row[colIndex] = update[1][key];
            }
            sheet.getRange(rowIndex + 1, 1, 1, row.length).setValues([row]);
        });
        return true;
    } catch (e) {
        Logger.log('Could not obtain lock: ' + e);
        return false;
    }
    finally {
        lock.releaseLock();
    }
}

function deleteRow(tableName, id) {
    const lock = LockService.getDocumentLock();
    try {
        lock.waitLock(30000);
        const sheet = SpreadsheetApp.getActive().getSheetByName(tableName);
        const data = sheet.getDataRange().getValues();
        const rowIndex = data.findIndex(row => String(row[0]) === String(id));
        if (rowIndex === -1) {
            throw new Error(`Row with id ${id} not found in table ${tableName}`);
        }
        sheet.deleteRow(rowIndex + 1);
        return true;
    } catch (e) {
        Logger.log('Could not obtain lock: ' + e);
        return false;
    }
    finally {
        lock.releaseLock();
    }
}

function Users() {
    return [Session.getActiveUser().getEmail(), Session.getEffectiveUser().getEmail()];
}

function getIdentity() {
    // CacheService.getUserCache().remove('identity');
    const email = Session.getActiveUser().getEmail().toLowerCase();
    // check if email is in the cache
    const cache = CacheService.getUserCache();
    var identity = cache.get('identity');
    if (identity)
        identity = JSON.parse(identity);
    else {
        const data = getData('הרשאות');
        const row = data.find(row => row[0].toLowerCase() === email);
        if (!row) {
            Logger.log(`Unauthorized access attempt by ${email}`);
            return null;
        }
        const id = row[1];
        const soldier = getDataById('כוח אדם', id);
        if (!soldier) {
            Logger.log(`No soldier found for user ${email} with id ${id}`);
            return null;
        }
        identity = {
            email,
            id,
            name: soldier ? `${soldier[1]} ${soldier[2]}` : 'Unknown',
        };
        cache.put('identity', JSON.stringify(identity), 3600); // cache for 1 hour
    }
    return identity;
}

function doGet(e) {
    // if ?page=page, return the page
    const identity = getIdentity();
    if (!identity) {
        return HtmlService.createHtmlOutputFromFile('unauthorized');
    }
    var page = e.parameter.page || 'dashboard';
    try {
        var template = HtmlService.createTemplateFromFile(page);
        template.scriptUrl = ScriptApp.getService().getUrl();
        return template.evaluate().setTitle(PAGES.find(p => p.id === page)?.name || 'רשמ"צ');
    } catch (error) {
        // טיפול במקרה שהקובץ לא קיים
        return HtmlService.createHtmlOutput("הדף לא נמצא: " + page);
    }
}

function include(filename) {
    return HtmlService.createHtmlOutputFromFile(filename).getContent();
}

function navbar(activePage) {
    const scriptUrl = ScriptApp.getService().getUrl();
    const identity = getIdentity();
    return `
    <nav>
        <ul>
            ${PAGES.map(page => `
                <li class="${page.id === activePage ? 'active' : ''}"><a href="${scriptUrl}?page=${page.id}" target="_top">${page.name}</a></li>
            `).join('')}
        </ul>
        <span class="user">שלום <strong>${identity.name}</strong>!</span>
    </nav>
    `;
}
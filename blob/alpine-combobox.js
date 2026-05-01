// ==========================================
// 1. הזרקת ה-CSS (זהה לקודם)
// ==========================================
function injectStyles() {
    if (document.getElementById('alpine-combobox-styles')) return;

    const style = document.createElement('style');
    style.id = 'alpine-combobox-styles';
    style.textContent = `
            .ac-wrapper { position: relative; width: 100%; font-family: inherit; box-sizing: border-box; }
            .ac-wrapper * { box-sizing: border-box; }
            .ac-input { width: 100%; padding: 10px 14px; border: 1px solid #d1d5db; border-radius: 8px; font-size: 16px; outline: none; transition: border-color 0.2s, box-shadow 0.2s; background-color: #fff; color: #111827; }
            .ac-input:focus { border-color: #3b82f6; box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1); }
            .ac-popover { top: anchor(bottom); justify-self: anchor-center; width: anchor-size(width); position-try-fallbacks: flip-block; margin: 6px 0; padding: 6px; border: 1px solid #e5e7eb; border-radius: 8px; background: white; box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.1); max-height: 40dvh; overflow-y: auto; overscroll-behavior: contain; }
            .ac-list { list-style: none; margin: 0; padding: 0; }
            .ac-item { padding: 12px 16px; min-height: 44px; display: flex; align-items: center; border-radius: 6px; cursor: pointer; transition: background-color 0.1s; color: #374151; }
            .ac-item:hover, .ac-item:active { background-color: #f3f4f6; }
            .ac-item-none { color: #6b7280; }
            .ac-item-empty { text-align: center; color: #9ca3af; cursor: default; justify-content: center; }
            .ac-item-empty:hover { background-color: transparent; }

            .ac-wrapper { position: relative; width: 100%; font-family: inherit; box-sizing: border-box; }
            .ac-wrapper * { box-sizing: border-box; }
            .ac-input { width: 100%; padding: 10px 14px; border: 1px solid #d1d5db; border-radius: 8px; font-size: 16px; outline: none; transition: border-color 0.2s, box-shadow 0.2s; background-color: #fff; color: #111827; }
            .ac-input:focus { border-color: #3b82f6; box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.1); }
            .ac-popover { top: anchor(bottom); justify-self: anchor-center; width: anchor-size(width); position-try-fallbacks: flip-block; margin: 6px 0; padding: 6px; border: 1px solid #e5e7eb; border-radius: 8px; background: white; box-shadow: 0 10px 15px -3px rgba(0, 0, 0, 0.1); max-height: 40dvh; overflow-y: auto; overscroll-behavior: contain; }
            .ac-list { list-style: none; margin: 0; padding: 0; }
            .ac-item { padding: 12px 16px; min-height: 44px; display: flex; align-items: center; border-radius: 6px; cursor: pointer; transition: background-color 0.1s; color: #374151; }
            .ac-item:hover, .ac-item:active { background-color: #f3f4f6; }

            /* קלאס חדש עבור פריט שמואר על ידי המקלדת */
            .ac-item-highlighted { background-color: #e5e7eb; }

            .ac-item-none { color: #6b7280; }
            .ac-item-empty { text-align: center; color: #9ca3af; cursor: default; justify-content: center; }
            .ac-item-empty:hover { background-color: transparent; }
        `;
    document.head.appendChild(style);
}

// ==========================================
// 2. לוגיקת Alpine.js (מעודכנת לסנכרון דו-כיווני)
// ==========================================
function createCombobox(listFn, displayProp) {
    return {
        searchQuery: '',
        selectedItem: null,
        popoverOpen: false,
        highlightedIndex: -1, // -1 אומר שאף פריט אינו מסומן (פרט לשדה עצמו)

        init() {
            this.$watch('popoverOpen', value => {
                const popoverEl = this.$refs.listbox;
                if (value) {
                    if (!popoverEl.matches(':popover-open')) popoverEl.showPopover();
                    // כשפותחים את הרשימה, נאפס את ההארה
                    this.highlightedIndex = -1;
                } else {
                    if (popoverEl.matches(':popover-open')) popoverEl.hidePopover();
                }
            });

            // כשהחיפוש משתנה, נאפס את ההארה כדי שלא נהיה מחוץ לגבולות המערך החדש
            this.$watch('searchQuery', () => {
                if (this.popoverOpen) {
                    this.highlightedIndex = -1;
                }
            });
        },

        get sourceList() {
            return listFn() || [];
        },

        get filteredItems() {
            const query = String(this.searchQuery).trim().toLowerCase();
            if (query === '') return this.sourceList;
            return this.sourceList.filter(item => {
                const text = String(displayProp ? item[displayProp] : item).toLowerCase();
                return text.includes(query);
            });
        },

        selectItem(item) {
            if (item) {
                this.selectedItem = item;
                this.searchQuery = displayProp ? item[displayProp] : item;
            } else {
                this.selectedItem = null;
                this.searchQuery = '';
            }

            this.popoverOpen = false;
            this.$refs.wrapper.blur();
        },

        // פונקציה חדשה: סנכרון מערך חיצוני לתוך הרכיב
        syncFromExternal(externalItem) {
            // מניעת לולאה אין סופית אם אין שינוי אמיתי
            if (this.selectedItem === externalItem && this.searchQuery !== '') return;

            this.selectedItem = externalItem;

            if (!externalItem) {
                this.searchQuery = '';
                return;
            }

            // מציאת הפריט ברשימה כדי להציג את השם שלו בשורת החיפוש
            // משתמשים ב-String כדי להימנע מבעיות של מספר מול מחרוזת
            const item = this.sourceList.find(i => i === externalItem);
            if (item) {
                this.searchQuery = String(displayProp ? item[displayProp] : item);
            } else {
                this.searchQuery = '';
            }
        },

        // --- לוגיקת מקלדת ---

        navigateDown() {
            if (!this.popoverOpen) {
                this.popoverOpen = true;
                return;
            }
            const maxIndex = this.filteredItems.length; // +1 אם מחשיבים את "ללא בחירה"
            // אם אנחנו בסוף הרשימה, לא נעשה כלום (או שנעשה לופ להתחלה, פה עשיתי עצירה)
            if (this.highlightedIndex < maxIndex) {
                this.highlightedIndex++;
                this.scrollToHighlighted();
            }
        },

        navigateUp() {
            if (!this.popoverOpen) return;
            if (this.highlightedIndex > -1) { // מאפשר לחזור לפוקוס רק על השדה עצמו (אינדקס -1)
                this.highlightedIndex--;
                this.scrollToHighlighted();
            }
        },

        selectHighlighted() {
            if (!this.popoverOpen) return;

            if (this.highlightedIndex === -1) {
                // אם המשתמש הקיש אנטר בלי לבחור מהרשימה, נוכל לסגור או לא לעשות כלום
                this.popoverOpen = false;
            } else if (this.highlightedIndex === 0 && this.filteredItems.length > 0) {
                // במקרה שלנו, הפריט הראשון במסך הוא "ללא בחירה", אז נטפל בו בנפרד (ראה הסבר ב-HTML)
            }
            else {
                // האינדקסים מוסטים ב-1 בגלל ה-li של "ללא בחירה"
                // נתקן זאת בפונקציה של ה-HTML, כדי שזה יעבוד נכון
            }
        },

        // פונקציית עזר לבחירה דרך המקלדת
        confirmSelection(e) {
            if (!this.popoverOpen) return;
            e.preventDefault(); // מונע מ-tab לעבור לשדה הבא או enter מלשלוח טופס

            // אנחנו מחשבים את האינדקס הכולל:
            // אינדקס 0: "ללא בחירה"
            // אינדקס 1 ומעלה: פריטים מסוננים

            if (this.highlightedIndex === 0) {
                this.selectItem(null);
            } else if (this.highlightedIndex > 0 && this.highlightedIndex <= this.filteredItems.length) {
                const selectedFromList = this.filteredItems[this.highlightedIndex - 1];
                this.selectItem(selectedFromList);
            } else {
                this.popoverOpen = false;
            }
        },

        escape(e) {
            e.preventDefault();
            this.popoverOpen = false;
            this.highlightedIndex = -1;
        },

        // מבטיח שהפריט המואר יהיה גלוי לעין בתוך גלילת ה-Popover
        scrollToHighlighted() {
            this.$nextTick(() => {
                const listbox = this.$refs.listbox;
                const highlightedEl = listbox.querySelector('.ac-item-highlighted');
                if (highlightedEl) {
                    highlightedEl.scrollIntoView({ block: 'nearest' });
                }
            });
        },
    };
}

// ==========================================
// 3. הגדרת ה-Custom Element
// ==========================================
class AlpineCombobox extends HTMLElement {
    connectedCallback() {
        injectStyles();

        const listName = this.getAttribute('list');
        // שאיבת המשתנה שאנחנו רוצים לקשור אליו (למשל form.manager)
        const bindTarget = this.getAttribute('selected-item');

        if (!listName)
            throw new Error('AlpineCombobox: יש לציין את מאפיין "list" עם שם המשתנה שמכיל את הרשימה.');
        if (!bindTarget)
            console.warn('AlpineCombobox: לא צויין selected-item. הסנכרון החיצוני לא יעבוד.');

        const keyProp = this.getAttribute('key-prop');
        const displayProp = this.getAttribute('display-prop');
        const placeholder = this.getAttribute('placeholder') || 'חיפוש...';

        const templateEl = this.querySelector('template');
        const itemTemplate = templateEl ? templateEl.innerHTML : `<span x-text="item${displayProp ? `.${displayProp}` : ''}"></span>`;

        const uniqueId = Math.random().toString(36).substring(2, 11);
        const anchorName = `--ac-anchor-${uniqueId}`;

        // בניית פקודות ה-init של Alpine להאזנה למשתנה החיצוני
        let initDirective = '';
        if (bindTarget) {
            initDirective = `
                    x-init="
                        $watch('selectedItem', val => { ${bindTarget} = val });
                        $watch('${bindTarget}', val => { syncFromExternal(val) });
                        syncFromExternal(${bindTarget});
                    "
                `;
        }

        // בניית ה-DOM (ללא ה-hidden input!)
        this.innerHTML = `
                <div 
                    x-data="createCombobox(() => (${listName}), ${displayProp ? `'${displayProp}'` : 'null'})" 
                    ${initDirective}
                    class="ac-wrapper"
                    @click.outside="popoverOpen = false"
                    @keydown.escape="escape()"
                    x-ref="wrapper"
                >
                    <input 
                        type="text" 
                        x-ref="searchInput"
                        x-model="searchQuery" 
                        @focus="popoverOpen = true"
                        @click="popoverOpen = true"
                        @input="popoverOpen = true"

                        @keydown.down.prevent="navigateDown()"
                        @keydown.up.prevent="navigateUp()"
                        @keydown.enter="confirmSelection($event)"
                        @keydown.tab="confirmSelection($event)"
                        
                        style="anchor-name: ${anchorName};"
                        class="ac-input"
                        placeholder="${placeholder}"
                    >
                    
                    <div 
                        popover="manual" 
                        x-ref="listbox" 
                        style="position-anchor: ${anchorName};"
                        class="ac-popover"
                    >
                        <ul class="ac-list">
                            <li @click="selectItem(null)" class="ac-item ac-item-none"
                                @mouseenter="highlightedIndex = 0"
                                :class="{'ac-item-highlighted': highlightedIndex === 0}">
                                ללא בחירה
                            </li>
                            
                            <template x-for="(item, index) in filteredItems" ${keyProp ? `:key="item.${keyProp}"` : ''}>
                                <li @click="selectItem(item)" class="ac-item"
                                    @mouseenter="highlightedIndex = index + 1"
                                    :class="{'ac-item-highlighted': highlightedIndex === index + 1}">
                                    ${itemTemplate}
                                </li>
                            </template>
                            
                            <li x-show="filteredItems.length === 0" class="ac-item ac-item-empty">
                                לא נמצאו תוצאות
                            </li>
                        </ul>
                    </div>
                </div>
            `;
    }
}

if (!customElements.get('alpine-combobox')) {
    customElements.define('alpine-combobox', AlpineCombobox);
}

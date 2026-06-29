function injectStyles() {
    if (document.getElementById('alpine-datatable-styles')) return;

    const style = document.createElement('style');
    style.id = 'alpine-datatable-styles';
    style.textContent = `
        alpine-datatable {}`;

    document.head.appendChild(style);
}

function createDataTable() {
    return {

    };
}

class AlpineDataTable extends HTMLElement {
    connectedCallback() {
        super.connectedCallback();
        injectStyles();

        const listName = this.getAttribute('list');
        if (!listName) {
            console.error('alpine-datatable requires a "list" attribute');
            return;
        }
        var cols = this.getAttribute('columns');
        if (!cols) {
            // TODO: generate columns from data if possible
            console.error('alpine-datatable requires a "columns" attribute');
            return;
        }
        const paging = this.getAttribute('paging'); // number of rows per page
        const selectedItem = this.getAttribute('selected-item'); // name of the variable to bind the selected item to
    }
}

if (!customElements.get('alpine-datatable')) {
    customElements.define('alpine-datatable', AlpineDataTable);
}
const owners = new WeakMap();

export function setOpen(dialog, open, owner) {
    if (!owners.has(dialog)) {
        dialog.addEventListener('cancel', event => {
            event.preventDefault();
            owners.get(dialog)?.invokeMethodAsync('CloseAuthDialog');
        });
    }
    owners.set(dialog, owner);
    if (open && !dialog.open) dialog.showModal();
    if (!open && dialog.open) dialog.close();
}

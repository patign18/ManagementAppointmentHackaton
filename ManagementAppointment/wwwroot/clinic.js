window.clinic = {
    openDialog: (element, reference) => {
        element.showModal();
        element.addEventListener('cancel', event => { event.preventDefault(); reference.invokeMethodAsync('Cancel'); });
    },
    download: (name, type, base64) => {
        const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], { type }));
        const link = document.createElement('a'); link.href = url; link.download = name;
        document.body.appendChild(link); link.click(); link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};

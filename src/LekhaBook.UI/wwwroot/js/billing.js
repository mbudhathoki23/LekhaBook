// Submits a signed server-generated POST form (used by eSewa ePay v2 checkout).
export function submitForm(actionUrl, fields) {
    const form = document.createElement('form');
    form.method = 'POST';
    form.action = actionUrl;
    for (const [name, value] of Object.entries(fields)) {
        const input = document.createElement('input');
        input.type = 'hidden';
        input.name = name;
        input.value = value ?? '';
        form.appendChild(input);
    }
    document.body.appendChild(form);
    form.submit();
}

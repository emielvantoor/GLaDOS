    function toggleInspector() {
        const inspector = document.getElementById('raw-inspector');
        const status = document.getElementById('inspector-status');
        if (inspector.style.display === 'block') {
            inspector.style.display = 'none';
            status.innerText = 'Collapsed';
        } else {
            inspector.style.display = 'block';
            status.innerText = 'Expanded';
        }
    }

    function handleKeyPress(e) {
        if (e.key === 'Enter' && !e.shiftKey) {
            e.preventDefault();
            sendPrompt();
        }
    }

    function updateTempVal(val) {
        document.getElementById('tempValue').innerText = parseFloat(val).toFixed(1);
    }

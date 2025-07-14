class NetworkAnimation {
    constructor() {
        this.canvas = document.getElementById('network-canvas');
        this.ctx = this.canvas.getContext('2d');
        this.mouseX = 0;
        this.mouseY = 0;
        this.nodes = [];
        this.animationPaused = false;
        this.animationId = null;
        
        this.init();
    }

    init() {
        this.setupEventListeners();
        this.resizeCanvas();
        this.createNodes();
        this.animate();
    }

    setupEventListeners() {

        document.addEventListener('mousemove', (e) => {
            this.mouseX = e.clientX;
            this.mouseY = e.clientY;
        });

        window.addEventListener('resize', () => {
            this.resizeCanvas();
            this.createNodes();
        });

        window.addEventListener('blur', () => {
            this.animationPaused = true;
            if (this.animationId) {
                cancelAnimationFrame(this.animationId);
            }
        });
        
        window.addEventListener('focus', () => {
            if (this.animationPaused) {
                this.animationPaused = false;
                this.animate();
            }
        });
    }

    resizeCanvas() {
        this.canvas.width = window.innerWidth;
        this.canvas.height = window.innerHeight;
    }

    createNodes() {
        this.nodes = [];
        const nodeCount = Math.floor((this.canvas.width * this.canvas.height) / 20000);
        
        for (let i = 0; i < nodeCount; i++) {
            this.nodes.push({
                x: Math.random() * this.canvas.width,
                y: Math.random() * this.canvas.height,
                vx: (Math.random() - 0.5) * 0.8,
                vy: (Math.random() - 0.5) * 0.8,
                size: Math.random() * 1.5 + 0.5
            });
        }
    }

    animate() {
        if (this.animationPaused) return;

        this.ctx.fillStyle = '#0a0a0a';
        this.ctx.fillRect(0, 0, this.canvas.width, this.canvas.height);

        this.nodes.forEach((node, i) => {
            const dx = this.mouseX - node.x;
            const dy = this.mouseY - node.y;
            const distance = Math.sqrt(dx * dx + dy * dy);
            
            if (distance < 150) {
                const force = (150 - distance) / 150 * 0.03;
                node.vx += dx * force * 0.001;
                node.vy += dy * force * 0.001;
            }

            node.x += node.vx;
            node.y += node.vy;

            if (node.x < 0 || node.x > this.canvas.width) node.vx *= -1;
            if (node.y < 0 || node.y > this.canvas.height) node.vy *= -1;

            this.ctx.beginPath();
            this.ctx.arc(node.x, node.y, node.size, 0, Math.PI * 2);
            this.ctx.fillStyle = distance < 100 ? '#ffffff' : 'rgba(255, 255, 255, 0.8)';
            this.ctx.fill();

            if (distance < 100) {
                this.ctx.beginPath();
                this.ctx.arc(node.x, node.y, node.size * 2, 0, Math.PI * 2);
                this.ctx.fillStyle = `rgba(255, 255, 255, ${0.2 * (1 - distance / 100)})`;
                this.ctx.fill();
            }

            this.nodes.slice(i + 1).forEach(otherNode => {
                const dx = otherNode.x - node.x;
                const dy = otherNode.y - node.y;
                const distance = Math.sqrt(dx * dx + dy * dy);

                if (distance < 100) {
                    this.ctx.beginPath();
                    this.ctx.moveTo(node.x, node.y);
                    this.ctx.lineTo(otherNode.x, otherNode.y);
                    this.ctx.strokeStyle = `rgba(255, 255, 255, ${(1 - distance / 100) * 0.6})`;
                    this.ctx.lineWidth = 1;
                    this.ctx.stroke();
                }
            });
        });

        this.animationId = requestAnimationFrame(() => this.animate());
    }

    pause() {
        this.animationPaused = true;
        if (this.animationId) {
            cancelAnimationFrame(this.animationId);
        }
    }

    resume() {
        if (this.animationPaused) {
            this.animationPaused = false;
            this.animate();
        }
    }

    destroy() {
        this.pause();
        document.removeEventListener('mousemove', this.mouseMoveHandler);
        window.removeEventListener('resize', this.resizeHandler);
        window.removeEventListener('blur', this.blurHandler);
        window.removeEventListener('focus', this.focusHandler);
    }
}

document.addEventListener('DOMContentLoaded', () => {

    setTimeout(() => {
        if (document.getElementById('network-canvas')) {
            window.networkAnimation = new NetworkAnimation();
        }
    }, 100);
});
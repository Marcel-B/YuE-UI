import { createApp } from 'vue'
import App from './App.vue'
import { locale } from './i18n'
import './style.css'

document.documentElement.lang = locale.value
createApp(App).mount('#app')

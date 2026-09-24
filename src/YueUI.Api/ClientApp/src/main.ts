import { createApp } from 'vue'
import PrimeVue from 'primevue/config'
import Aura from '@primeuix/themes/aura'
import Button from 'primevue/button'
import Card from 'primevue/card'
// Shadows the browser's global DataView (the ArrayBuffer view) in this module; without the import the registration
// below silently picks up that constructor, and rendering fails with "Constructor DataView requires 'new'".
import DataView from 'primevue/dataview'
import App from './App.vue'
import { locale } from './i18n'
import './style.css'
import Select from 'primevue/select'
import InputText from 'primevue/inputtext'
import ProgressBar from 'primevue/progressbar'
import SelectButton from 'primevue/selectbutton'
import Divider from 'primevue/divider'
import Fieldset from 'primevue/fieldset'
import Tooltip from 'primevue/tooltip'
import Menubar from 'primevue/menubar'
import Tag from 'primevue/tag'
import FloatLabel from 'primevue/floatlabel'
import Textarea from 'primevue/textarea'

const app = createApp(App)
app.use(PrimeVue, {
  theme: {
    preset: Aura,
    // PrimeVue's styles go into their own cascade layer, so Tailwind utilities (a later layer) can override them.
    // The order itself is declared at the top of style.css.
    options: { cssLayer: { name: 'primevue', order: 'theme, base, primevue, components, utilities' } },
  },
})
app
  .component('Button', Button)
  .component('Card', Card)
  .component('DataView', DataView)
  .component('Select', Select)
  .component('InputText', InputText)
  .component('Textarea', Textarea)
  .component('ProgressBar', ProgressBar)
  .component('SelectButton', SelectButton)
  .component('Fieldset', Fieldset)
  .component('Divider', Divider)
  .component('Menubar', Menubar)
  .component('FloatLabel', FloatLabel)
  .component('Tag', Tag)

app.directive('tooltip', Tooltip)

document.documentElement.lang = locale.value
app.mount('#app')

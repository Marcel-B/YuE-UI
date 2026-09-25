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
import Menu from 'primevue/menu'
import Tag from 'primevue/tag'
import FloatLabel from 'primevue/floatlabel'
import Textarea from 'primevue/textarea'
import ConfirmDialog from 'primevue/confirmdialog'
import Dialog from 'primevue/dialog'
import ConfirmationService from 'primevue/confirmationservice'
import Tabs from 'primevue/tabs'
import TabList from 'primevue/tablist'
import Tab from 'primevue/tab'
import TabPanels from 'primevue/tabpanels'
import TabPanel from 'primevue/tabpanel'
import Badge from 'primevue/badge'
import Timeline from 'primevue/timeline'

const app = createApp(App)
app.use(PrimeVue, {
  theme: {
    preset: Aura,
    // PrimeVue's styles go into their own cascade layer, so Tailwind utilities (a later layer) can override them.
    // The order itself is declared at the top of style.css.
    options: { cssLayer: { name: 'primevue', order: 'theme, base, primevue, components, utilities' } },
  },
})
// Asks before anything is deleted; App.vue holds the one ConfirmDialog.
app.use(ConfirmationService)
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
  .component('Menu', Menu)
  .component('FloatLabel', FloatLabel)
  .component('Tag', Tag)
  .component('ConfirmDialog', ConfirmDialog)
  .component('Dialog', Dialog)
  .component('Tabs', Tabs)
  .component('TabList', TabList)
  .component('Tab', Tab)
  .component('TabPanels', TabPanels)
  .component('TabPanel', TabPanel)
  .component('Badge', Badge)
  .component('Timeline', Timeline)

app.directive('tooltip', Tooltip)

document.documentElement.lang = locale.value
app.mount('#app')

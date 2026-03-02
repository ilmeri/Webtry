using UnityEngine;
using UnityEngine.InputSystem;
using System.Collections;
using UnityEngine.UI;

/// <summary>
/// PlanePhysics – Retry‑henkinen 2D‑lentokonefysiikka FLIP‑mekaniikalla ja ammuksilla.
/// Tämä versio tukee 3D‑mallin visuaalista pyöräytystä flipin yhteydessä (spinni 180°).
/// </summary>
[RequireComponent(typeof(Rigidbody2D))]
public class PlanePhysics : MonoBehaviour
{
    private CharacterSelectionUI characterUI;
    public void LinkCharacterUI(CharacterSelectionUI ui)
    {
        characterUI = ui;
    }
    [Header("Työntö ja nosto")]
    [SerializeField] private float flyPower = 12f;
    [SerializeField] private float forwardAccel = 1.5f;

    [Header("Pitch")]
    [SerializeField] private float pitchUpTorque = 2.5f;
    [SerializeField] private float pitchDownTorqueMax = 2f;
    [SerializeField] private float downTorqueExponent = 2f;
    [SerializeField] private float pitchDownLimit = -45f;
    [SerializeField] private float minPitchSpeed = 3f;
    [SerializeField] private float maxAngularSpeed = 120f;
    

    [Header("Rajoitukset & vastus")]
    [SerializeField] private float maxSpeed = 12f;
    [SerializeField] private float linearDrag = 0.35f;
    [SerializeField] private float angularDrag = 2f;

    [Header("Ammusasetukset")]
    [SerializeField] private GameObject bulletPrefab;
    private float nextFireTime;

    [Header("Visuaalinen 3D‑malli")]
    [SerializeField] private Transform visualModel;

    [Header("Aika-asetukset")]
    [SerializeField] private TMPro.TextMeshProUGUI speedDisplay;

    [Header("Input Actions")]
    private PlayerInput playerInput;
    private InputAction throttleAction;
    private InputAction shootPrimaryAction;
    private bool isFiringPrimary;
    private bool isFiringSecondary;
    private InputAction shootSecondaryAction;
    private InputAction flipAction;
    private InputAction resetAction;

    [Header("Damage Visual FX")]
    [SerializeField] private ParticleSystem damageLevel1;
    [SerializeField] private ParticleSystem damageLevel2;
    [SerializeField] private ParticleSystem damageLevel3;

    [Header("Throttle FX")]
    [SerializeField] private ParticleSystem throttleParticles;

    [Header("VFX")]
[SerializeField] private ParticleSystem dodgeEffectLeft;
[SerializeField] private ParticleSystem dodgeEffectRight;
    [SerializeField] private GameObject hitEffectPrefab;
    [SerializeField] private GameObject crashEffectPrefab;
    [SerializeField] private GameObject explosionPrefab;

    [Header("Health")]
    [SerializeField] private int maxHealth = 10;
    private int health;
    
    public int CurrentHealth => health;
    public int MaxHealth => maxHealth;
    [Header("Physics Materials")]
    [SerializeField] private PhysicsMaterial2D noBounceMaterial;
    private Rigidbody2D rb;
    [Header("Wheels")]
    [SerializeField] private Collider2D[] wheelColliders;
    private WeaponHandler weaponHandler;
    private bool isCrashed = false;
    private bool throttleHeld;
    private bool flipped;
    private bool isFlipping = false;
    private InputAction dodgeAction;
    private bool isDodging = false;
    private Coroutine flipCoroutine;
    private Coroutine dodgeCoroutine;
    private float dodgeCooldown = 2f;
    private float lastDodgeTime = -999f;
    private int ammo = 0;
    private const int maxAmmo = 20;
    public int CurrentAmmo => ammo;
    public int MaxAmmo => maxAmmo;
    
    private PhysicsMaterial2D originalPhysicsMaterial;
    private Collider2D bodyCollider;
    private bool hasExploded = false;
    public int PlayerIndex { get; private set; }
    public PlayerInput PlayerInput => playerInput;

    private bool isLanded = false;

    private void Awake()
{
    rb = GetComponent<Rigidbody2D>();
    weaponHandler = GetComponent<WeaponHandler>();
    rb.gravityScale = 1f;
    rb.linearDamping = linearDrag;
    rb.angularDamping = angularDrag;

    // Säädä painopistettä alemmaksi (esim. -0.5f y-akselilla)
    rb.centerOfMass = new Vector2(0f, -0.5f);

    bodyCollider = GetComponent<Collider2D>();
    if (bodyCollider != null)
        originalPhysicsMaterial = bodyCollider.sharedMaterial;

    health = maxHealth;
}

    private void OnEnable() { }

    private void OnDisable()
    {
        shootPrimaryAction.started -= ctx => isFiringPrimary = true;
        shootPrimaryAction.canceled -= ctx => isFiringPrimary = false;

        shootSecondaryAction.started -= ctx => isFiringSecondary = true;
        shootSecondaryAction.canceled -= ctx => isFiringSecondary = false;

        flipAction.performed -= ctx => ToggleFlip();
        resetAction.performed -= ctx => ResetPlane();

        throttleAction.Disable();
        shootPrimaryAction.Disable();
        shootSecondaryAction.Disable();
        flipAction.Disable();
        resetAction.Disable();
    }

    private void Update()
    {if (playerInput == null) return;
        if (characterUI != null)
            characterUI.UpdateHealthFromPlane(CurrentHealth, MaxHealth);

        if (throttleAction != null && throttleAction.enabled)
            throttleHeld = throttleAction.ReadValue<float>() > 0.1f;
        else
            throttleHeld = Input.GetKey(KeyCode.Space) || Input.GetMouseButton(0);

        if (throttleParticles != null)
        {
            var emission = throttleParticles.emission;
            emission.enabled = throttleHeld;
        }

        if (dodgeAction != null && flipAction != null)
        {
            bool dodgeHeld = dodgeAction.ReadValue<float>() > 0.5f;
            bool flipPressed = flipAction.triggered;
        
            if (dodgeHeld && flipPressed && !isFlipping)
            {
                Debug.Log("[DODGE INPUT] Dodge held + Flip pressed → triggering TryDodge");
                TryDodge();
            }
            else if (flipPressed && !isDodging)
            {
                Debug.Log("[FLIP INPUT] Flip pressed without Dodge → triggering ToggleFlip");
                ToggleFlip();
            }
        }

        ShootAuto();

        if (Input.GetMouseButtonDown(1)) ToggleFlip();
        if (Input.GetMouseButtonDown(2)) weaponHandler.FireSlot(0, true, this);

        if (Keyboard.current.minusKey.wasPressedThisFrame)
        {
            Time.timeScale = Mathf.Max(0.1f, Time.timeScale - 0.1f);
            Time.fixedDeltaTime = 0.02f * Time.timeScale;
            if (speedDisplay) speedDisplay.text = $"Speed: {Time.timeScale:0.00}x";
        }
        if (Keyboard.current[Key.Equals].wasPressedThisFrame)
        {
            Time.timeScale = Mathf.Min(2f, Time.timeScale + 0.1f);
            Time.fixedDeltaTime = 0.02f * Time.timeScale;
            if (speedDisplay) speedDisplay.text = $"Speed: {Time.timeScale:0.00}x";
        }

        if (isCrashed && visualModel != null && !IsGrounded())
            visualModel.Rotate(Vector3.right * 300f * Time.deltaTime);

        if (isCrashed && IsGrounded() && !hasExploded)
        {
            hasExploded = true;
            StartCoroutine(DelayedReset());
        }
    }

    private void ToggleFlip()
    {
    if (isFlipping) return;

    flipped = !flipped;
    isFlipping = true;

    if (visualModel != null)
    {
        if (flipCoroutine != null) StopCoroutine(flipCoroutine);
        flipCoroutine = StartCoroutine(AnimateFlip());
    }
    }

    private IEnumerator AnimateFlip()
    {
        Debug.Log($"[AnimateFlip] Flip aloitettu. flipped={flipped}");
    
        float duration = 0.4f;
        float elapsed = 0f;
        Quaternion startRot = visualModel.localRotation;
        Quaternion endRot = Quaternion.Euler(flipped ? 180f : 0f, 0f, 0f);
    
        while (elapsed < duration)
        {
            float t = elapsed / duration;
            t = 1f - Mathf.Pow(1f - t, 3f);
            visualModel.localRotation = Quaternion.Slerp(startRot, endRot, t);
            elapsed += Time.deltaTime;
            yield return null;
        }
    
        visualModel.localRotation = endRot;
        isFlipping = false;
    
        Debug.Log("[AnimateFlip] Flip valmis.");
    }

        private void FixedUpdate()
    {
        // Jos lentokone on syöksytilassa, ei tehdä mitään
        if (isCrashed)
            return;

        // Tarkista ollaanko maassa renkailla
        bool wheelsGrounded = IsWheelsGrounded();

        if (wheelsGrounded)
        {
            float z = transform.eulerAngles.z;
            z = (z + 360f) % 360f;
            float deltaTo0 = Mathf.Abs(Mathf.DeltaAngle(z, 0f));
            float deltaTo180 = Mathf.Abs(Mathf.DeltaAngle(z, 180f));

            Debug.Log($"[SUORISTUS] (INSTANT) z={z:0.00}, deltaTo0={deltaTo0:0.00}, deltaTo180={deltaTo180:0.00}, flipped={flipped}");

            // Valitaan suoraan se pystyyn-asento, joka on lähempänä
            if (deltaTo0 < deltaTo180)
            {
                Debug.Log("[SUORISTUS] (INSTANT) Lukitaan suoraan 0 asteeseen");
                transform.eulerAngles = new Vector3(0f, 0f, 0f);
            }
            else
            {
                Debug.Log("[SUORISTUS] (INSTANT) Lukitaan suoraan 180 asteeseen");
                transform.eulerAngles = new Vector3(0f, 0f, 180f);
            }
        }

        // Jos ollaan maassa ja kone on oikein päin ja vauhti on pieni, asetetaan landed-tila
        if (wheelsGrounded && Mathf.Abs(rb.linearVelocity.x) < 0.5f && Mathf.Abs(rb.linearVelocity.y) < 0.5f && Mathf.Abs(transform.up.y) > 0.7f)
        {
            if (!isLanded)
            {
                isLanded = true;
                rb.gravityScale = 0f;
                rb.linearDamping = 5f;
                rb.angularDamping = 5f;
                rb.linearVelocity = Vector2.zero;
                rb.angularVelocity = 0f;
                // bodyCollider.sharedMaterial = noBounceMaterial; // POISTETTU, ei enää tarvita
                Debug.Log("[PlanePhysics] Landed!");
            }

           

            // Jos painetaan kaasua, lähdetään liikkeelle
            if (throttleHeld)
            {
                isLanded = false;
                rb.gravityScale = 1f;
                rb.linearDamping = linearDrag;
                rb.angularDamping = angularDrag;
                // bodyCollider.sharedMaterial = originalPhysicsMaterial; // POISTETTU, ei enää tarvita
                Debug.Log("[PlanePhysics] Takeoff by throttle!");
                // Anna fysiikan jatkua heti!
            }
            else
            {
                return; // Ei tehdä muuta fysiikkaa kun ollaan laskeutuneena
            }
        }
        else if (isLanded && !wheelsGrounded)
        {
            // Jos lähdetään taas ilmaan
            isLanded = false;
            rb.gravityScale = 1f;
            rb.linearDamping = linearDrag;
            rb.angularDamping = angularDrag;
            // bodyCollider.sharedMaterial = originalPhysicsMaterial; // POISTETTU, ei enää tarvita
            Debug.Log("[PlanePhysics] Takeoff!");
        }

        // Jos ollaan laskeutuneena, ei tehdä muuta
        if (isLanded)
        {
            // Jos painetaan kaasua, lähdetään liikkeelle
            if (throttleHeld)
            {
                isLanded = false;
                rb.gravityScale = 1f;
                rb.linearDamping = linearDrag;
                rb.angularDamping = angularDrag;
                // bodyCollider.sharedMaterial = originalPhysicsMaterial; // POISTETTU, ei enää tarvita
                Debug.Log("[PlanePhysics] Takeoff by throttle!");
            }
            else
            {
                return; // Ei tehdä muuta fysiikkaa kun ollaan laskeutuneena
            }
        }

        // Lasketaan lentokoneen eteenpäin suuntautuva nopeus
        float forwardSpeed = Vector2.Dot(rb.linearVelocity, transform.right);
        // Määritetään pystysuuntainen merkki (käänteinen, jos kone on "flipped")
        float vertSign = flipped ? -1f : 1f;
    
        // Jos kaasua pidetään pohjassa
        if (throttleHeld)
        {
            // Lasketaan nostovoima suhteessa koneen suuntaan
            Vector2 upDir = transform.up * vertSign;
            float upDot = Vector2.Dot(upDir.normalized, Vector2.up);
            float directionalLift = Mathf.Clamp01(upDot);
    
            // Lisätään nostovoima ja eteenpäin suuntautuva työntövoima
            rb.AddForce(upDir * flyPower * directionalLift, ForceMode2D.Force);
            rb.AddRelativeForce(Vector2.right * forwardAccel, ForceMode2D.Force);
        }
    
        // Jos eteenpäin suuntautuvaa nopeutta on riittävästi
        if (forwardSpeed > 0.01f)
        {
            // Lasketaan aerodynaaminen nostovoima
            float lift = 0.15f * forwardSpeed * forwardSpeed;
            Vector2 liftDir = transform.up * vertSign;
            float liftDot = Vector2.Dot(liftDir.normalized, Vector2.up);
            lift *= Mathf.Clamp01(liftDot);
            rb.AddForce(liftDir * lift, ForceMode2D.Force);
        }
    
        // Jos kone ei ole maassa
        if (!IsGrounded())
        {
            // Lasketaan kulma pystysuunnasta
            float reference = flipped ? -90f : 90f;
            float angleFromUp = Mathf.DeltaAngle(reference, transform.eulerAngles.z);
            Debug.Log($"[PlanePhysics] Kulma: reference={reference}, eulerZ={transform.eulerAngles.z}, angleFromUp={angleFromUp}");
    
            // Jos kaasua pidetään pohjassa ja nopeus ylittää miniminopeuden
            if (throttleHeld && forwardSpeed >= minPitchSpeed)
            {
                // Lisätään nousuvääntöä, jos kulmanopeus ei ylitä maksimiarvoa
                if (Mathf.Abs(rb.angularVelocity) < maxAngularSpeed)
                    rb.AddTorque(pitchUpTorque * vertSign, ForceMode2D.Force);
            }
            // Jos kaasua ei pidetä pohjassa
            else if (!throttleHeld)
            {
                bool nearGround = IsNearGround(3f);
                Debug.Log($"[PlanePhysics] PitchDown: nearGround={nearGround}, forwardSpeed={forwardSpeed}, angleFromUp={angleFromUp}");

                if (nearGround)
                {
                    Debug.Log("[PlanePhysics] PitchDown estetty, koska ollaan lähellä maata.");
                    rb.linearDamping = 0.6f; // Hidastaa vauhtia nopeammin lähellä maata

                    // ÄLÄ lisää pitch down -vääntöä, mutta kaikki muu (esim. ilmanvastus, kulman hidastuminen) toimii normaalisti
                    // Eli: älä tee tässä mitään muuta, mutta älä käytä returnia!
                }
                else
                {
                    rb.linearDamping = linearDrag; // Palautetaan normaali vastus

                    // Lasketaan laskuvääntö suhteessa kulmaan
                    float factor = Mathf.Pow(Mathf.InverseLerp(0f, 90f, Mathf.Abs(angleFromUp)), downTorqueExponent);
                    float torque = pitchDownTorqueMax * factor;
                    float dir = (angleFromUp > 0 ? 1f : -1f) * vertSign;

                    Debug.Log($"[PlanePhysics] PitchDown: factor={factor}, torque={torque}, dir={dir}");

                    // Lisätään laskuvääntöä, jos kulma ylittää rajan ja kulmanopeus ei ole liian suuri
                    if (angleFromUp > pitchDownLimit && Mathf.Abs(rb.angularVelocity) < maxAngularSpeed)
                    {
                        Debug.Log("[PlanePhysics] PitchDown: Lisätään vääntö!");
                        rb.AddTorque(dir * torque, ForceMode2D.Force);
                    }
                    else
                    {
                        Debug.Log("[PlanePhysics] PitchDown: Ei lisätä vääntöä (ehdot eivät täyty).");
                    }
                }
            }
        }
    
        // Rajoitetaan kulmanopeus maksimiarvoon
        if (Mathf.Abs(rb.angularVelocity) > maxAngularSpeed)
            rb.angularVelocity = Mathf.Sign(rb.angularVelocity) * maxAngularSpeed;
    
        // Rajoitetaan lineaarinen nopeus maksimiarvoon
        if (rb.linearVelocity.magnitude > maxSpeed)
            rb.linearVelocity = rb.linearVelocity.normalized * maxSpeed;
    }

    private bool IsGrounded()
    {
        ContactPoint2D[] contacts = new ContactPoint2D[2];
        int n = rb.GetContacts(contacts);
        for (int i = 0; i < n; i++)
            if (Vector2.Dot(contacts[i].normal, Vector2.up) > 0.5f)
                return true;
        return false;
    }

 private void OnCollisionEnter2D(Collision2D coll)
{
    // Jos yksikään rengas on maassa törmäyshetkellä, ei damagea
    if (IsWheelsGrounded())
    {
        Debug.Log("[OnCollisionEnter2D] Renkaat maassa törmäyksessä – ei damagea!");
        return;
    }

    // ...vanha damage-logiikka jatkuu tästä...
    rb.angularVelocity = 0f;

    if (hitEffectPrefab != null && coll.contacts.Length > 0)
    {
        Vector2 contactPoint = coll.contacts[0].point;
        Instantiate(hitEffectPrefab, contactPoint, Quaternion.identity);
    }

    if (!isCrashed)
    {
        health--;
        UpdateDamageVisuals();

        if (health <= 0)
        {
            EnterCrashMode();
        }
    }

    if (isCrashed && IsGrounded() && !hasExploded)
    {
        hasExploded = true;
        StartCoroutine(DelayedReset());
    }
}

     private void ShootAuto()
    {
        var weapon0 = weaponHandler.GetWeapon(0);
        //Debug.Log($"[ShootAuto] Weapon 0: autoFire={weapon0?.autoFire}, isFiringPrimary={isFiringPrimary}");
        if (weapon0?.autoFire == true && isFiringPrimary)
        {
            //Debug.Log("[ShootAuto] Ammutaan aseella 0 (autoFire)");
            weaponHandler.FireSlot(0, true, this);
        }

        var weapon1 = weaponHandler.GetWeapon(1);
        if (weapon1?.autoFire == true && isFiringSecondary)
        {
            //Debug.Log("[ShootAuto] Ammutaan aseella 1 (autoFire)");
            weaponHandler.FireSlot(1, true, this);
        }
    }

    private void TryShoot(int index)
    {
        Debug.Log($"[TryShoot] Yritetään ampua slotista {index}");
        var weapon = weaponHandler.GetWeapon(index);
        if (weapon == null)
        {
            Debug.Log("[TryShoot] Ei asetta kyseisessä slotissa");
            return;
        }

        if (weapon.autoFire)
        {
            Debug.Log("[TryShoot] Aseessa on autoFire – ei ammuta tässä");
            return;
        }

        Debug.Log("[TryShoot] Ammutaan yksittäinen laukaus");
        weaponHandler.FireSlot(index, true, this);
    
    }

    private void TryDodge()
    {
        if (isDodging)
        {
            Debug.Log("[TryDodge] Hylätty: jo dodging-tilassa.");
            return;
        }
    
        if (Time.time < lastDodgeTime + dodgeCooldown)
        {
            Debug.Log($"[TryDodge] Hylätty: cooldown käynnissä ({Time.time - lastDodgeTime:0.00}s sitten).");
            return;
        }
    
        if (visualModel == null)
        {
            Debug.Log("[TryDodge] Hylätty: visualModel puuttuu.");
            return;
        }
    
        Debug.Log("[TryDodge] Käynnistetään dodge-spin.");
    
        if (dodgeCoroutine != null) StopCoroutine(dodgeCoroutine);
        dodgeCoroutine = StartCoroutine(DodgeSpin());
    }

   private IEnumerator DodgeSpin()
{
    bool emissionDisabled = false; // Tila, joka seuraa, onko partikkeliefektien emissio poistettu käytöstä
    isDodging = true; // Asetetaan dodging-tila päälle
    lastDodgeTime = Time.time; // Päivitetään viimeisin dodge-aika

    // 🔛 Aktivoidaan vasemman ja oikean puolen dodge-efektit, jos ne on määritelty
    if (dodgeEffectLeft != null)
    {
        var emission = dodgeEffectLeft.emission;
        emission.enabled = true;
    }
    if (dodgeEffectRight != null)
    {
        var emission = dodgeEffectRight.emission;
        emission.enabled = true;
    }

    float duration = 1f; // Dodgen kesto
    float effectDuration = 1f; // Efektien kesto
    float elapsed = 0f; // Ajanseuranta

    float baseAngle = flipped ? 180f : 0f; // Peruskulma riippuen siitä, onko kone käännetty
    float startX = baseAngle; // Aloituskulma
    float endX = baseAngle + 360f; // Lopetuskulma (täysi kierros)

    Debug.Log($"[DodgeSpin] startX={startX}, endX={endX}, flipped={flipped}");

    // Pyöritetään konetta ja hallitaan efektejä dodgen aikana
    while (elapsed < duration)
    {
        float t = elapsed / duration; // Normalisoitu aika (0–1)
        float smoothT = 1f - Mathf.Pow(1f - t, 3f); // Smoothstep-interpolaatio
        float currentX = Mathf.Lerp(startX, endX, smoothT); // Lasketaan nykyinen kulma
        visualModel.localEulerAngles = new Vector3(currentX % 360f, 0f, 0f); // Päivitetään visuaalinen malli

        // Lisätään impulssi ensimmäisellä framella
        if (elapsed <= Time.deltaTime)
        {
            float dodgeImpulse = 4f; // Dodgen impulssin voimakkuus
            rb.AddRelativeForce(Vector2.right * dodgeImpulse, ForceMode2D.Impulse); // Lisätään impulssi
        }

        // Lasketaan partikkelien koko suhteessa aikaan
        float sizeT = Mathf.InverseLerp(0f, duration, elapsed);
        float particleSize = Mathf.Lerp(0.1f, 0.4f, 1f - Mathf.Abs(sizeT - 0.5f) * 2f);

        // Päivitetään vasemman ja oikean puolen partikkelien koko
        if (dodgeEffectLeft != null)
        {
            var main = dodgeEffectLeft.main;
            main.startSize = particleSize;
        }
        if (dodgeEffectRight != null)
        {
            var main = dodgeEffectRight.main;
            main.startSize = particleSize;
        }

        // Sammutetaan emissio, kun efektin kesto on saavutettu
        if (!emissionDisabled && elapsed >= effectDuration)
        {
            if (dodgeEffectLeft != null)
            {
                var emission = dodgeEffectLeft.emission;
                emission.enabled = false;
            }
            if (dodgeEffectRight != null)
            {
                var emission = dodgeEffectRight.emission;
                emission.enabled = false;
            }
            emissionDisabled = true; // Merkitään emissio poistetuksi
        }

        elapsed += Time.deltaTime; // Päivitetään kulunut aika
        yield return null; // Odotetaan seuraavaa framea
    }

    // Palautetaan visuaalinen malli alkuperäiseen kulmaan
    visualModel.localEulerAngles = new Vector3(baseAngle, 0f, 0f);
    isDodging = false; // Poistetaan dodging-tila

    // 🧹 Sammutetaan efektit ja palautetaan partikkelien koko
    if (dodgeEffectLeft != null)
    {
        var emission = dodgeEffectLeft.emission;
        emission.enabled = false;
        var main = dodgeEffectLeft.main;
        main.startSize = 0.1f;
    }
    if (dodgeEffectRight != null)
    {
        var emission = dodgeEffectRight.emission;
        emission.enabled = false;
        var main = dodgeEffectRight.main;
        main.startSize = 0.1f;
    }

    Debug.Log("[DodgeSpin] Spin valmis.");
}



    private void ResetPlane()
    {
        if (playerInput == null) return;

        health = maxHealth;
        ammo = maxAmmo;
        isCrashed = false;
        isDodging = false;
        flipped = false;
        isFlipping = false;

        if (visualModel != null)
            visualModel.localRotation = Quaternion.identity;

        rb.gravityScale = 1f;
        rb.angularDamping = angularDrag;
        rb.linearDamping = linearDrag;
        rb.linearVelocity = Vector2.zero;
        rb.angularVelocity = 0f;

        var spawner = FindFirstObjectByType<PlayerSpawner>();
        if (spawner != null)
        {
            var newPos = spawner.GetSpawnPosition(playerInput.playerIndex);
            transform.position = newPos.position;
            transform.rotation = newPos.rotation;
        }

        UpdateDamageVisuals();

        if (bodyCollider != null)
            bodyCollider.sharedMaterial = originalPhysicsMaterial; // Tämänkin voi poistaa, jos et halua palauttaa materiaalia resetissä

        if (dodgeEffectLeft != null)
        {
            var emission = dodgeEffectLeft.emission;
            emission.enabled = false;
        }
        if (dodgeEffectRight != null)
        {
            var emission = dodgeEffectRight.emission;
            emission.enabled = false;
        }

        hasExploded = false;
}

    public void AddAmmo(int amount)
    {
        ammo = Mathf.Clamp(ammo + amount, 0, maxAmmo);
    }

  private void EnterCrashMode()
{
    isCrashed = true;
    rb.gravityScale = 2f;
    rb.angularDamping = 0.1f;
    rb.linearDamping = 0.1f;

    if (bodyCollider != null)
        bodyCollider.sharedMaterial = null; // Tämänkin voi poistaa, jos ei tarvita

        // 💥 Pieni visuaalinen efekti kun kone tuhoutuu ilmassa
    if (crashEffectPrefab != null)
        Instantiate(crashEffectPrefab, transform.position, Quaternion.identity);

}

   public void TakeDamage(int amount, int attackerIndex)
{
    if (isCrashed || isDodging) return;

    health -= amount;
    UpdateDamageVisuals();

    if (health <= 0)
    {
        EnterCrashMode();

        // Lisätään pisteitä vain, jos pelaaja on tuhoutunut
        if (attackerIndex >= 0)
        {
            Debug.Log($"[Plane] Kone tuhoutui! Lisätään piste pelaajalle {attackerIndex}");    
            ScoreManager.Instance?.AddScore(attackerIndex, 1);
        }
    }
}

    private void UpdateDamageVisuals()
    {
        float healthPct = (float)health / maxHealth;

        if (damageLevel1 != null)
        {
            var emission = damageLevel1.emission;
            emission.enabled = healthPct <= 0.7f;
        }
        if (damageLevel2 != null)
        {
            var emission = damageLevel2.emission;
            emission.enabled = healthPct <= 0.4f;
        }
        if (damageLevel3 != null)
        {
            var emission = damageLevel3.emission;
            emission.enabled = healthPct <= 0.15f;
        }
    }

    public void Heal(int amount)
    {
        if (health >= maxHealth || isCrashed) return;

        health = Mathf.Min(health + amount, maxHealth);
        UpdateDamageVisuals();

        if (characterUI != null)
            characterUI.UpdateHealthFromPlane(CurrentHealth, MaxHealth);

        Debug.Log($"[PlanePhysics] Heal({amount}) → nykyinen HP: {health}/{maxHealth}");
    }


private IEnumerator DelayedReset()
{
    //yield return new WaitForSeconds(1f); // Räjähdysefekti
    // spawn räjähdys tähän kohtaan jos haluat
    Instantiate(explosionPrefab, transform.position, Quaternion.identity);

    yield return new WaitForSeconds(2f); // Odotetaan ennen resettiä
    ResetPlane();
}
    public void AssignPlayer(PlayerInput input)
    {
        playerInput = input;
        PlayerIndex = input.playerIndex;

        var map = input.actions;
        throttleAction = map["Throttle"];
        shootPrimaryAction = map["Shoot"];
        shootSecondaryAction = map["ShootSecondary"];
        flipAction = map["Flip"];
        resetAction = map["Reset"];
        dodgeAction = map["Dodge"];

        throttleAction.Enable();
        shootPrimaryAction.Enable();
        shootSecondaryAction.Enable();
        flipAction.Enable();
        resetAction.Enable();
        dodgeAction.Enable();
        
        shootPrimaryAction.started += ctx => isFiringPrimary = true;
        shootPrimaryAction.canceled += ctx => isFiringPrimary = false;

        shootSecondaryAction.started += ctx => isFiringSecondary = true;
        shootSecondaryAction.canceled += ctx => isFiringSecondary = false;

        // flipAction.performed += ctx => ToggleFlip();
        resetAction.performed += ctx => ResetPlane();
        //dodgeAction.performed += ctx => TryDodge();

        shootPrimaryAction.performed += ctx => TryShoot(0);

        shootSecondaryAction.performed += ctx => TryShoot(1);

        Debug.Log($"[PlanePhysics] AssignPlayer called for player {PlayerIndex}");
        Debug.Log($"[PlanePhysics] health={health}, isCrashed={isCrashed}");

        if (weaponHandler != null)
            Debug.Log($"[PlanePhysics] AssignPlayer valmis, PlayerIndex={PlayerIndex}");
        }

          public void ApplyColor(Color color)
    {
        if (visualModel == null)
        {
            Debug.LogWarning("[ApplyColor] visualModel on null");
            return;
        }

        var renderers = visualModel.GetComponentsInChildren<Renderer>();
        Debug.Log($"[ApplyColor] Löytyi {renderers.Length} renderöijää visualModelin alta");

        foreach (var renderer in renderers)
        {
            var mats = renderer.materials;
            for (int i = 0; i < mats.Length; i++)
            {
                Debug.Log($"[ApplyColor] {renderer.name} → Materiaali {i}: {mats[i].name}");

                if (mats[i].name.Contains("Body"))
                {
                    mats[i].color = color;
                    Debug.Log($"[ApplyColor] → Väri asetettu materiaalille {mats[i].name} renderöijässä {renderer.name}");
                }
            }
            renderer.materials = mats;
        }

        // UI-taustan värin asetusta ei enää tehdä täällä, koska se tehdään PlayerRootin Initialize-metodissa
        
    }
       
    

private bool IsNearGround(float distance = 100f)
{
    // Suuntaa raycast koneen "alapuolelle" riippuen flipistä
    Vector2 downDir = flipped ? transform.up : -transform.up;
    Vector2 origin = (Vector2)transform.position + downDir * 0.2f;
    int groundMask = LayerMask.GetMask("Ground");

    Debug.DrawLine(origin, origin + downDir * distance, Color.yellow, 0.5f);
    Debug.Log($"[IsNearGround] Raycast origin: {origin}, suunta: {downDir}, pituus: {distance}");

    RaycastHit2D hit = Physics2D.Raycast(origin, downDir, distance, groundMask);

    if (hit.collider != null && hit.collider != bodyCollider)
    {
        Debug.DrawLine(origin, origin + downDir * distance, Color.green, 0.1f);
        Debug.Log($"[IsNearGround] Osui groundiin! Etäisyys maahan: {hit.distance:0.00} (max {distance})");
        return true;
    }
    Debug.DrawLine(origin, origin + downDir * distance, Color.red, 0.1f);
    Debug.Log("[IsNearGround] Ei groundia lähellä.");
    return false;
}

private bool IsWheelsGrounded()
{
    foreach (var wheel in wheelColliders)
    {
        ContactPoint2D[] contacts = new ContactPoint2D[2];
        int n = wheel.GetContacts(contacts);
        for (int i = 0; i < n; i++)
            if (Vector2.Dot(contacts[i].normal, Vector2.up) > 0.5f)
                return true;
    }
    return false;
}
}

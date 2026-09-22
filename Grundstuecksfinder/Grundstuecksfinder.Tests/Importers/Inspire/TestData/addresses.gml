<?xml version="1.0" encoding="UTF-8"?>
<wfs:FeatureCollection xmlns:wfs="http://www.opengis.net/wfs/2.0"
                        xmlns:gml="http://www.opengis.net/gml/3.2"
                        xmlns:xlink="http://www.w3.org/1999/xlink"
                        xmlns:gn="http://inspire.ec.europa.eu/schemas/gn/4.0"
                        xmlns="http://inspire.ec.europa.eu/schemas/ad/4.0"
                        numberMatched="4" numberReturned="4">
  <wfs:member>
    <Address gml:id="Address_1">
      <position>
        <GeographicPosition>
          <geometry>
            <gml:Point gml:id="Address_1_pos" srsName="http://www.opengis.net/def/crs/epsg/0/25832" srsDimension="2">
              <gml:pos>560050 5990050</gml:pos>
            </gml:Point>
          </geometry>
        </GeographicPosition>
      </position>
      <locator>
        <AddressLocator>
          <designator>
            <LocatorDesignator>
              <designator>12</designator>
              <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
            </LocatorDesignator>
          </designator>
        </AddressLocator>
      </locator>
      <component xlink:href="#ThoroughfareName_1"/>
      <component xlink:href="#PostalDescriptor_1"/>
      <component xlink:href="#AdminUnitName_land"/>
      <component xlink:href="#AdminUnitName_gemeinde"/>
    </Address>
  </wfs:member>
  <wfs:member>
    <Address gml:id="Address_2">
      <position>
        <GeographicPosition>
          <geometry>
            <gml:Point gml:id="Address_2_pos" srsName="http://www.opengis.net/def/crs/epsg/0/25832" srsDimension="2">
              <gml:pos>560250 5990050</gml:pos>
            </gml:Point>
          </geometry>
        </GeographicPosition>
      </position>
      <locator>
        <AddressLocator>
          <designator>
            <LocatorDesignator>
              <designator>4</designator>
              <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumber"/>
            </LocatorDesignator>
          </designator>
          <designator>
            <LocatorDesignator>
              <designator>a</designator>
              <type xlink:href="http://inspire.ec.europa.eu/codelist/LocatorDesignatorTypeValue/addressNumberExtension"/>
            </LocatorDesignator>
          </designator>
        </AddressLocator>
      </locator>
      <component xlink:href="#ThoroughfareName_2"/>
      <component xlink:href="#PostalDescriptor_2"/>
      <component xlink:href="#AdminUnitName_land"/>
      <component xlink:href="#AdminUnitName_gemeinde"/>
      <component xlink:href="#AdminUnitName_ortsteil"/>
    </Address>
  </wfs:member>
  <wfs:member>
    <Address gml:id="Address_3_dangling">
      <position>
        <GeographicPosition>
          <geometry>
            <gml:Point gml:id="Address_3_pos" srsName="http://www.opengis.net/def/crs/epsg/0/25832" srsDimension="2">
              <gml:pos>999000 5999000</gml:pos>
            </gml:Point>
          </geometry>
        </GeographicPosition>
      </position>
      <!-- References a component id that isn't present in additionalObjects: must resolve to null, not throw. -->
      <component xlink:href="#ThoroughfareName_missing"/>
    </Address>
  </wfs:member>
  <wfs:member>
    <Address gml:id="Address_4_nopoint">
      <!-- No position/geometry at all: must be skipped entirely, since there's nothing to join. -->
      <component xlink:href="#ThoroughfareName_1"/>
    </Address>
  </wfs:member>
  <wfs:additionalObjects>
    <wfs:SimpleFeatureCollection>
      <wfs:member>
        <ThoroughfareName gml:id="ThoroughfareName_1">
          <name>
            <gn:GeographicalName>
              <gn:spelling>
                <gn:SpellingOfName>
                  <gn:text>Am Kirchhof</gn:text>
                </gn:SpellingOfName>
              </gn:spelling>
            </gn:GeographicalName>
          </name>
        </ThoroughfareName>
      </wfs:member>
      <wfs:member>
        <ThoroughfareName gml:id="ThoroughfareName_2">
          <name>
            <gn:GeographicalName>
              <gn:spelling>
                <gn:SpellingOfName>
                  <gn:text>Dorfstraße</gn:text>
                </gn:SpellingOfName>
              </gn:spelling>
            </gn:GeographicalName>
          </name>
        </ThoroughfareName>
      </wfs:member>
      <wfs:member>
        <PostalDescriptor gml:id="PostalDescriptor_1">
          <postCode>24649</postCode>
        </PostalDescriptor>
      </wfs:member>
      <wfs:member>
        <PostalDescriptor gml:id="PostalDescriptor_2">
          <postCode>24601</postCode>
        </PostalDescriptor>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_land">
          <alternativeIdentifier>01</alternativeIdentifier>
          <name>
            <gn:GeographicalName>
              <gn:spelling>
                <gn:SpellingOfName>
                  <gn:text>Schleswig-Holstein</gn:text>
                </gn:SpellingOfName>
              </gn:spelling>
            </gn:GeographicalName>
          </name>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_gemeinde">
          <alternativeIdentifier>01060099</alternativeIdentifier>
          <name>
            <gn:GeographicalName>
              <gn:spelling>
                <gn:SpellingOfName>
                  <gn:text>Beispielgemeinde</gn:text>
                </gn:SpellingOfName>
              </gn:spelling>
            </gn:GeographicalName>
          </name>
        </AdminUnitName>
      </wfs:member>
      <wfs:member>
        <AdminUnitName gml:id="AdminUnitName_ortsteil">
          <alternativeIdentifier>010600990015</alternativeIdentifier>
          <name>
            <gn:GeographicalName>
              <gn:spelling>
                <gn:SpellingOfName>
                  <gn:text>Beispielortsteil</gn:text>
                </gn:SpellingOfName>
              </gn:spelling>
            </gn:GeographicalName>
          </name>
        </AdminUnitName>
      </wfs:member>
    </wfs:SimpleFeatureCollection>
  </wfs:additionalObjects>
</wfs:FeatureCollection>
